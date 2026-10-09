// ── Home/Facet.UI.cs — the facet head: title lead · views (the facet words) + Following · busy ProgressBar · InfoBar ──
//
// Role: UI
// Wave: fifth pass (docs/plans/wavee/home-rebuild-implementation.md "Fifth pass — stock controls"): replaces
//   `FacetPivot.UI.cs` outright. Every piece here is a STOCK engine control — nothing hand-rolled. Sidebar rework (P4): the
//   facet words are no longer a private 28-px title pivot but the page's VIEWS, the standard `Design.PageViewsStyle`
//   SelectorBar under a real "Home" page title, so Home has the same TitleViews head (164 DIP) as every other pivot page.
//
// ── The head (component tree) ────────────────────────────────────────────────────────────────────────────────────────
//   FacetRow (props re-pushed: Embed.Comp(props, factory))
//   └─ column  (hoisted = PageHead.HoistedFor("home"), which reads Shell.Ui.PresentedNavStyle)
//      ├─ LEAD  h = Facet.LeadFor(hoisted)  ClipToBounds  Animate = PageHead.Reflow
//      │    non-hoisted: [HeadTop · "Home" title · TitleToMeta · reserved meta slot · HeadToViewsGap]; hoisted: 12, empty
//      ├─ BAND  h = FacetRowH (51)
//      │    ├─ row  h = SelectorH (48) [ PageHead.Views(labels, Index, onChange, trailing: Following) ]  (empty when hoisted)
//      │    └─ 3-px box { ProgressBar.Indeterminate(width: NaN) }   Opacity ← Switch.Bar (compositor bind, no re-render)
//      ├─ BelowBarGap spacer (9)
//      └─ Flow.Show(Phase == Failed, InfoBar.Create(Error, "Couldn't load <facet>", …, Retry))
//
// The body therefore starts at LeadFor + FacetRowH + BelowBarGap: 164 (PageHeadRules.Extent(TitleViews)), or 72 when hoisted
// (Extent(Hoisted)). The band is pinned with `.Sticky(Facet.StuckInset, scope: Facet.PageScope)` ONLY when not hoisted
// (transparent, no acrylic — the page column names itself `Facet.PageScope`, so the pin holds for the whole page and not
// just this component's own column). Hoisted, the words and Following live in the Zune band's second row (published
// through `PageHead.Publish`), the band is a plain 51-DIP spacer holding only the busy bar, and nothing in Home is sticky
// but the chapter headers. `HomeScreen` cuts the content column under the pinned band with
// `.StickyClip(Facet.ContentClipFor(hoisted))` + a `WhileStuck` top feather; `Zones.UI.cs` reads `Facet.StuckBottomFor` for
// its chapter headers' stick inset. The InfoBar sits BELOW the gap, unpinned — it scrolls with the page's flow and, when it
// grows, pushes the content (never overlays it).
//
// ── LAYOUT STABILITY ─────────────────────────────────────────────────────────────────────────────────────────────────
// (a) Every height is a pure function of `hoisted`; no data input. The row keeps its 48 DIP before the words arrive and
//     the words fade in place (PageHead.Views), so the body top is the same y in every data state.
// (b) `hoisted` is the PRESENTED nav style, so the hoist (lead 104 -> 12, words leaving the page) lands in the quiet commit
//     HoistSettleMs after the frame moved, where PageHead.Reflow is not snapped by the card's descendant suppression.
// (c) The facet bar has one data-free key and the stable `Index` signal, so the pill slides on a facet switch.
//
// Selection: the SelectorBar OWNS `Index` (the caller's `Signal<int>`, written on click/roving before OnChange fires);
// `HomeScreen` re-syncs it from `FacetPivot.Resolve(words, Switch.Target)` in an effect so a failed/no-op switch snaps
// the selection back. A word click selects `FacetPivot.Target(word, FollowingMemory(word.Id))` — the sub-chip when Following
// was last on for that word — so the toggle's memory survives leaving and re-entering the word (Facets.cs).

using System;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Effects;
using FluentGpu.Signals;
using Wavee;
using static FluentGpu.Dsl.Ui;

namespace Wavee.HomeUi;

/// <summary>Re-pushed props for <see cref="FacetRow"/> (<c>Embed.Comp(props, factory)</c>). The two signals are the
/// SAME long-lived objects across <c>HomeScreen</c>'s re-renders (its per-tab fields); the row reads them in
/// <c>Render()</c> and re-renders on a genuine change only.</summary>
/// <param name="Words">The pivot's words, "All" first (<c>FacetPivot.Words</c>).</param>
/// <param name="Switch">The facet-switch machine — <c>Target</c> is the selected facet, <c>Bar</c> drives the busy
/// bar, <c>Phase == Failed</c> the InfoBar.</param>
/// <param name="Index">The SelectorBar's selected index — owned by <c>HomeScreen</c>, written by the bar on click and
/// re-synced from <c>Switch.Target</c> by the screen's own effect.</param>
/// <param name="Select">Selects a facet id (a word's id or its Following sub-chip id) through <c>HomeScreen</c>'s
/// one select path (verdict + state machine + fetch + history push). Nothing here touches <c>Entities</c>/<c>Shell</c>.</param>
/// <param name="Dismiss">Closes the failure InfoBar (<c>FacetSwitch.Dismiss</c>).</param>
/// <param name="FollowingMemory">"Was Following last on for this word id?" — read-only; decides which target a plain
/// word click carries.</param>
public sealed record FacetRowProps(
    IReadSignal<FacetWord[]> Words,
    IReadSignal<FacetSwitchState> Switch,
    Signal<int> Index,
    Action<string> Select,
    Action Dismiss,
    Func<string, bool> FollowingMemory);

/// <summary>The constants the page and the zones share: the facet band's geometry, the lead above it, the sticky offsets
/// that follow from whether the head is hoisted, and the sticky scope name. All pure; every height is expressed through
/// <see cref="PageGeometry"/> / <see cref="PageHeadRules"/>.</summary>
public static class Facet
{
    /// <summary>The views row's height: the page views bar's whole box (<see cref="PageGeometry.ViewsBarH"/>, 48).</summary>
    public const float SelectorH = PageGeometry.ViewsBarH;

    /// <summary>The busy bar's slot: WinUI's 3-px ProgressBar, part of the band so a switch started deep in the page still
    /// shows its progress.</summary>
    public const float BarH = 3f;

    /// <summary>The band's height (views row + busy bar) — the page's clip inset and the base of every chapter header's
    /// stick inset (<c>Zones.UI.cs</c>).</summary>
    public const float FacetRowH = SelectorH + BarH;

    /// <summary>How far below the viewport's top edge the band pins: a breath so the pinned words never sit flush
    /// against the page surface's rounded top (the page ScrollView draws no top edge cue — its own StickyClip + WhileStuck
    /// feathers do the dissolving, the Artist page's precedent).</summary>
    public const float StuckInset = Spacing.M;

    /// <summary>The pinned band's LOWER edge in the viewport — the page content's clip line and every chapter header's
    /// stick inset while the head is NOT hoisted. See <see cref="StuckBottomFor"/>.</summary>
    public const float StuckBottom = StuckInset + FacetRowH;

    /// <summary>The gap from the busy bar to the body: the views-to-body gap less the bar's own slot, so the body starts
    /// <see cref="PageGeometry.ViewsToBodyGap"/> under the views row (9).</summary>
    public const float BelowBarGap = PageGeometry.ViewsToBodyGap - BarH;

    /// <summary>The page column's <c>ScrollScope</c> name: the band pins against it (so the pin holds for the whole page
    /// and not for <see cref="FacetRow"/>'s own column, which is where a scope-less <c>.Sticky</c> would release).</summary>
    public const string PageScope = "home:page";

    /// <summary>The lead box's height above the band: the hoisted strip's top air (12), else the title head down to the
    /// views row (<c>PageHeadRules.Lead(TitleViews)</c> + <see cref="PageGeometry.HeadToViewsGap"/> = 104). Depends on
    /// <paramref name="hoisted"/> only, never on data.</summary>
    public static float LeadFor(bool hoisted)
        => hoisted ? PageGeometry.HoistedTop : PageHeadRules.Lead(PageHeadKind.TitleViews) + PageGeometry.HeadToViewsGap;

    /// <summary>The viewport line the chapter headers pin at. With nothing pinned above them (hoisted) they pin where the
    /// band would, at <see cref="StuckInset"/>; otherwise just under the pinned band.</summary>
    public static float StuckBottomFor(bool hoisted) => hoisted ? StuckInset : StuckBottom;

    /// <summary>The viewport line the content column cuts itself at: nothing is pinned when hoisted (0), else the band's
    /// lower edge.</summary>
    public static float ContentClipFor(bool hoisted) => hoisted ? 0f : StuckBottom;

    internal static readonly MotionTokenDef s_fade =
        MotionTokenDef.Eased(Design.Motion.Faster, Easing.FluentStandard, ReducedMotionPolicy.KeepFade);

    /// <summary>The Following toggle, shared by the page's views row and the Zune band's second row (published to it as
    /// <c>PageViewsPublication.Trailing</c>): ONE builder, so the two can never disagree. Built every call; <c>Flow.Show</c>
    /// only MOUNTS it while the selected word has a sub-chip, so the "no sub-chip" default word never reaches the screen.
    /// Value-controlled: the checked state is the switch machine's own resolution. Subscribes to the words and the switch.</summary>
    public static Element Following(FacetRowProps p)
    {
        var words = p.Words.Value;
        var (idx, followingOn) = FacetPivot.Resolve(words, p.Switch.Value.Target);
        bool hasSub = (uint)idx < (uint)words.Length && words[idx].HasSub;
        var word = (uint)idx < (uint)words.Length ? words[idx] : default;

        Element toggle = ToggleButton.Controlled(
            word.SubLabel ?? Loc.Get(Strings.Home.Following), followingOn,
            on => p.Select(on && word.SubId is not null ? word.SubId : word.Id),
            glyph: Icons.Friends)
            with
            {
                Enter = new EnterExit(Opacity: 0f, Active: true),
                Exit = new EnterExit(Opacity: 0f, Active: true),
                Transition = s_fade,
            };
        return Flow.Show(() => hasSub, toggle);
    }
}

/// <summary>The Following toggle as its own component, for the Zune band: it subscribes to Home's words and switch here, so
/// a facet-switch state change re-renders only this subtree and not the whole shell band that hosts it.</summary>
public sealed class FollowingHost : Component
{
    public override Element Render() => Facet.Following(UseProps<FacetRowProps>());
}

/// <summary>The facet head — see the file header for the tree. A component so it re-renders on <see cref="FacetRowProps.Words"/>
/// / <see cref="FacetRowProps.Switch"/> changes without re-rendering the whole screen.</summary>
public sealed class FacetRow : Component
{
    public override Element Render()
    {
        var p = UseProps<FacetRowProps>();
        var words = p.Words.Value;
        var state = p.Switch.Value;
        // The PRESENTED nav style (never the live one): the hoist lands HoistSettleMs after the frame moved.
        bool hoisted = PageHead.HoistedFor("home");

        var labels = new string[words.Length];
        for (int i = 0; i < labels.Length; i++) labels[i] = words[i].Label;

        void OnWord(int i)
        {
            var ws = p.Words.Peek();
            if ((uint)i >= (uint)ws.Length) return;
            p.Select(FacetPivot.Target(ws[i], p.FollowingMemory(ws[i].Id)));
        }

        // The LEAD: the page title and its reserved meta slot above the views, 104 tall, or the hoisted strip's 12 empty
        // DIP. Its height depends on `hoisted` alone and Reflows (a quiet commit), so the band and body ease with it.
        Element[] leadKids = hoisted
            ? Array.Empty<Element>()
            : new Element[]
            {
                Spacer(PageGeometry.HeadTop),
                PageHead.TitleRow(Loc.Get(Strings.Nav.Home), null) with
                {
                    Key = "home:facet:title", Enter = PageHead.FadeIn, Exit = PageHead.FadeOut, Transition = Facet.s_fade,
                },
                Spacer(PageGeometry.TitleToMeta),
                PageHead.MetaSlot(null),
                Spacer(PageGeometry.HeadToViewsGap),
            };
        Element lead = new BoxEl
        {
            Key = "home:facet:lead", Direction = 1, Height = Facet.LeadFor(hoisted), Shrink = 0f, MinWidth = 0f,
            ClipToBounds = true, Animate = PageHead.Reflow, Children = leadKids,
        };

        // The views row: always 48 tall. The words sit in PageHead's shared views row (one data-free bar key, so the pill
        // slides; empty labels keep the row and fade the words in place); Following ends the row. Hoisted, the row is
        // empty — the words moved to the Zune band — and the views fade out in place.
        Element[] rowKids = hoisted
            ? Array.Empty<Element>()
            : new Element[]
            {
                PageHead.Views(labels, p.Index, OnWord, Facet.Following(p), placeholder: null, scroll: false, key: "home:facet") with
                {
                    Key = "home:facet:views", Enter = PageHead.FadeIn, Exit = PageHead.FadeOut, Transition = Facet.s_fade,
                },
            };
        Element row = new BoxEl
        {
            Key = "home:facet:row", Direction = 0, AlignItems = FlexAlign.Center, Height = Facet.SelectorH, Shrink = 0f,
            MinWidth = 0f, Children = rowKids,
        };

        // The stretched indeterminate bar, always mounted: its opacity is a compositor bind on Switch.Bar so the fade
        // plays both ways without a re-render.
        var busy = new BoxEl
        {
            Key = "home:facet:busy",
            Height = Facet.BarH, MinWidth = 0f, HitTestVisible = false,
            Opacity = Prop.Of(() => p.Switch.Value.Bar ? 1f : 0f),
            Transition = Facet.s_fade,
            Children = [ProgressBar.Indeterminate(width: float.NaN)],
        };

        string failedLabel = state.FailedTarget is { } ft ? LabelOf(words, ft) ?? ft : "";
        string failedTarget = state.FailedTarget ?? "";
        Element failure = Flow.Show(() => p.Switch.Value.Phase == FacetPhase.Failed, new BoxEl
        {
            Key = "home:facet:failure",
            Direction = 1, MinWidth = 0f, Padding = new Edges4(0f, 0f, 0f, Spacing.M),
            Animate = PageHead.Reflow,
            Enter = new EnterExit(Dy: -8f, Opacity: 0f, Active: true),
            Exit = new EnterExit(Opacity: 0f, Active: true),
            Transition = MotionTokenDef.Eased(Design.Motion.Fast, Easing.FluentStandard, ReducedMotionPolicy.KeepFade),
            Children =
            [
                InfoBar.Create(InfoBarSeverity.Error,
                    Strings.Home.CouldntLoad(failedLabel), Loc.Get(Strings.Home.CheckConnection),
                    onClose: p.Dismiss,
                    actionButton: Button.Create(Loc.Get(Strings.Common.Retry), () => p.Select(failedTarget), ButtonAppearance.Standard)),
            ],
        });

        // The band: views row + busy bar, exactly FacetRowH tall, transparent. Pinned against the PAGE column
        // (Facet.PageScope), which HomeScreen names — a scope-less Sticky would clamp to this component's own column — but
        // only while not hoisted: hoisted, it is a plain spacer (the 72-DIP strip) and nothing in Home sticks but the chapters.
        var band = new BoxEl
        {
            Key = "home:facet:band",
            Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Height = Facet.FacetRowH, Shrink = 0f,
            Children = [row, busy],
        };

        return new BoxEl
        {
            Key = "home:facet",
            Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Children = [lead, hoisted ? band : band.Sticky(Facet.StuckInset, scope: Facet.PageScope), Spacer(Facet.BelowBarGap), failure],
        };
    }

    static string? LabelOf(FacetWord[] words, string id)
    {
        foreach (var w in words)
        {
            if (w.Id == id) return w.Label;
            if (w.HasSub && w.SubId == id) return w.SubLabel;
        }
        return null;
    }
}
