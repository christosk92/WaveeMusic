// ── Home/Facet.UI.cs — the facet row: SelectorBar (page-title style) + Following ToggleButton + busy ProgressBar + InfoBar ──
//
// Role: UI
// Wave: fifth pass (docs/plans/wavee/home-rebuild-implementation.md "Fifth pass — stock controls"): replaces
//   `FacetPivot.UI.cs` (the hand pivot `WordBox`, its roving keyboard model, the compact `FacetBand`, the private
//   `FollowingToggle` style, the context slot) outright. Every piece here is a STOCK engine control — nothing
//   hand-rolled. Seventh pass: the facet words ARE the page title (there is no greeting headline), so the stock
//   SelectorBar takes `Design.FacetTitleStyle` (28/36 display, rest secondary, selected primary semibold, no pill, a
//   subtle hover plate, 40-px items) through its `style` parameter instead of its default 14-px pill look.
//
// ── The row (component tree) ─────────────────────────────────────────────────────────────────────────────────────
//   FacetRow (props re-pushed: Embed.Comp(props, factory))
//   └─ column
//      ├─ HStack h=SelectorH(40) [ SelectorBar.Create(labels, Index, onChange, style: Design.FacetTitleStyle) · spacer
//      │                           · Flow.Show(hasSub, ToggleButton.Controlled) ]
//      ├─ 3-px box  { ProgressBar.Indeterminate(width: NaN) }   Opacity ← Switch.Bar (compositor bind, no re-render)
//      └─ Flow.Show(Phase == Failed, InfoBar.Create(Error, "Couldn't load <facet>", …, Retry))
//
// The HStack + the 3-px bar form the BAND, exactly `FacetRowH` tall, pinned with `.Sticky(0, scope: Facet.PageScope)`
// (transparent, no acrylic — the page column names itself `Facet.PageScope`, so the pin holds for the whole page and
// not just this component's own column). `HomeScreen` cuts the content column under it with
// `.StickyClip(Facet.FacetRowH)` + a `WhileStuck` top feather (Browse.Page.cs's masthead idiom); `Zones.UI.cs` reads the
// same constant for its chapter headers' stick inset. The InfoBar sits BELOW the band, unpinned — it scrolls with the
// page's flow and, when it grows, pushes the content (never overlays it).
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

/// <summary>The constants the page and the zones share: the pinned facet band's height and the sticky scope name.</summary>
public static class Facet
{
    /// <summary>The selector HStack's height: <c>Design.FacetTitleStyle</c>'s item height (the prototype's 40-px facet
    /// row — a 28/36 display word in a 40-px hover plate, no pill slot). The row is set to exactly this and centres the
    /// bar in it, so the constant is authoritative for the sticky clip inset rather than a measured value.</summary>
    public const float SelectorH = 40f;

    /// <summary>The busy bar's slot: WinUI's 3-px ProgressBar, part of the pinned band so a switch started deep in the
    /// page still shows its progress.</summary>
    public const float BarH = 3f;

    /// <summary>The pinned band's height (selector row + busy bar) — the page's <c>StickyClip</c> inset and the base of
    /// every chapter header's stick inset (<c>Zones.UI.cs</c>).</summary>
    public const float FacetRowH = SelectorH + BarH;

    /// <summary>How far below the viewport's top edge the band pins: a breath so the pinned words never sit flush
    /// against the page surface's rounded top (the page ScrollView draws no top edge cue — its own StickyClip + WhileStuck
    /// feathers do the dissolving, the Artist page's precedent).</summary>
    public const float StuckInset = Spacing.M;

    /// <summary>The pinned band's LOWER edge in the viewport — the page content's clip line and every chapter header's
    /// stick inset (<c>Zones.UI.cs</c>). The one number all three share.</summary>
    public const float StuckBottom = StuckInset + FacetRowH;

    /// <summary>The page column's <c>ScrollScope</c> name: the band pins against it (so the pin holds for the whole page
    /// and not for <see cref="FacetRow"/>'s own column, which is where a scope-less <c>.Sticky</c> would release).</summary>
    public const string PageScope = "home:page";
}

/// <summary>The facet row — see the file header for the tree. A component so it re-renders on <see cref="FacetRowProps.Words"/>
/// / <see cref="FacetRowProps.Switch"/> changes without re-rendering the whole screen.</summary>
public sealed class FacetRow : Component
{
    static readonly MotionTokenDef s_fade =
        MotionTokenDef.Eased(Design.Motion.Faster, Easing.FluentStandard, ReducedMotionPolicy.KeepFade);

    public override Element Render()
    {
        var p = UseProps<FacetRowProps>();
        var words = p.Words.Value;
        var state = p.Switch.Value;

        var labels = new string[words.Length];
        for (int i = 0; i < labels.Length; i++) labels[i] = words[i].Label;

        var (idx, followingOn) = FacetPivot.Resolve(words, state.Target);
        bool hasSub = (uint)idx < (uint)words.Length && words[idx].HasSub;
        var word = (uint)idx < (uint)words.Length ? words[idx] : default;

        // The page title: the stock bar in the facet-title style (no pill; the selected word is the semibold primary one),
        // one rung smaller when the row is too narrow for the title words (FacetForm).
        void OnWord(int i)
        {
            var ws = p.Words.Peek();
            if ((uint)i >= (uint)ws.Length) return;
            p.Select(FacetPivot.Target(ws[i], p.FollowingMemory(ws[i].Id)));
        }

        // Built every render; Flow.Show only MOUNTS it while hasSub is true, so the "no sub-chip" default word never
        // reaches the screen. Value-controlled: the checked state is the switch machine's own resolution.
        Element following = ToggleButton.Controlled(
            word.SubLabel ?? Loc.Get(Strings.Home.Following), followingOn,
            on => p.Select(on && word.SubId is not null ? word.SubId : word.Id),
            glyph: Icons.Friends)
            with
            {
                Enter = new EnterExit(Opacity: 0f, Active: true),
                Exit = new EnterExit(Opacity: 0f, Active: true),
                Transition = s_fade,
            };

        // The band (a column) cross-stretches the Responsive box, so it measures the row's full width and builds the row
        // in the form that width holds.
        Element row = Responsive.Of(w => new BoxEl
        {
            Key = "home:facet:row",
            Direction = 0, AlignItems = FlexAlign.Center, Height = Facet.SelectorH, MinWidth = 0f,
            Children =
            [
                SelectorBar.Create(labels, p.Index, onChange: OnWord,
                    style: FacetForm.IsTitle(w) ? Design.FacetTitleStyle : Design.FacetCompactStyle),
                new BoxEl { Grow = 1f, MinWidth = 0f },
                Flow.Show(() => hasSub, following),
            ],
        }, fallback: FacetForm.TitleMinWidth);

        // The stretched indeterminate bar, always mounted: its opacity is a compositor bind on Switch.Bar so the fade
        // plays both ways without a re-render.
        var busy = new BoxEl
        {
            Key = "home:facet:bar",
            Height = Facet.BarH, MinWidth = 0f, HitTestVisible = false,
            Opacity = Prop.Of(() => p.Switch.Value.Bar ? 1f : 0f),
            Transition = s_fade,
            Children = [ProgressBar.Indeterminate(width: float.NaN)],
        };

        string failedLabel = state.FailedTarget is { } ft ? LabelOf(words, ft) ?? ft : "";
        string failedTarget = state.FailedTarget ?? "";
        Element failure = Flow.Show(() => p.Switch.Value.Phase == FacetPhase.Failed, new BoxEl
        {
            Key = "home:facet:failure",
            Direction = 1, MinWidth = 0f, Padding = new Edges4(0f, Spacing.S, 0f, 0f),
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

        // The pinned band: selector row + bar, exactly FacetRowH tall, transparent. It pins against the PAGE column
        // (Facet.PageScope), which HomeScreen names — a scope-less Sticky would clamp to this component's own column.
        var band = new BoxEl
        {
            Key = "home:facet:band",
            Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Height = Facet.FacetRowH,
            Children = [row, busy],
        }.Sticky(Facet.StuckInset, scope: Facet.PageScope);

        return new BoxEl
        {
            Key = "home:facet",
            Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Children = [band, failure],
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
