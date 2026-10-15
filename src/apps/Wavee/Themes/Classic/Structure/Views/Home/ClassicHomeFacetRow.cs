// ── Themes/Classic/Structure/Views/Home/ClassicHomeFacetRow.cs — Classic's `Home.FacetRow` (themes plan 6.1, T10a1) ─────────
//
// From `Home\Facet.UI.cs`: the facet row — SelectorBar (page-title style) + Following ToggleButton + busy ProgressBar, and the failure
// InfoBar's layer (`FailureLayer`, which the PAGE places, tuning H1). Every piece
// is a STOCK engine control — nothing hand-rolled. The facet words ARE the page title (there is no greeting headline), so the stock
// SelectorBar takes `R.Type.FacetTitleStyle` (28/36 display, rest secondary, selected primary semibold, no pill, a subtle hover plate,
// 40-px items) through its `style` parameter instead of its default 14-px pill look.
//
// ── The row (element tree) ───────────────────────────────────────────────────────────────────────────────────────────
//   column
//   ├─ band  (Sticky) column h=FacetRowH
//   │  ├─ HStack h=SelectorH(40) [ SelectorBar.Create(labels, Index, onChange, style: R.Type.FacetTitleStyle) · spacer
//   │  │                           · Flow.Show(hasSub, ToggleButton.Controlled) ]
//   │  └─ 3-px slot { Flow.Show(Switch.Bar, ProgressBar.Indeterminate(width: NaN) fading in and out) }   (the slot never moves)
//
//   FailureLayer (a component the page lays OVER the top of its content, tuning H1)
//   └─ pass-through column { Flow.Show(Phase == Failed, InfoBar.Create(Error, "Couldn't load <facet>", …, Retry)) }
//
// The HStack + the 3-px bar form the BAND, exactly `FacetRowH` tall, pinned with `.Sticky(StuckInset, scope: PageScope)` (transparent, no
// acrylic — the page column names itself `PageScope`, so the pin holds for the whole page and not just this row's own column). The page
// cuts the content column under it with `.StickyClip(StuckBottom)` + a `WhileStuck` top feather (`ClassicBrowseDirectory`'s masthead idiom); the
// zones read the same constants for their chapter headers' stick inset.
//
// THE FAILURE SLOT (tuning H1): a failed facet switch reverts to the published facet and says so in an InfoBar. The bar used to sit in
// the flow under the band and pushed every zone down by its height; now it has its own slot, a layer the page stacks over the top edge
// of its content (`ClassicHomePage`), so the content below never moves when the bar comes or goes. The layer is pass-through: only
// the bar itself takes the pointer. It is unpinned like the content it lies on: it scrolls away with the page's top.
//
// The row is a view slot, so it re-renders on `Words` / `Switch` changes without re-rendering the whole screen. Selection: the SelectorBar
// OWNS `Index` (the host's cell, written on click/roving before OnChange fires); the host re-syncs it from the switch machine, so a failed or
// no-op switch snaps the selection back. A word click selects the sub-chip when Following was last on for that word (`FollowingMemory`).

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scroll.Effects;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.HomeUi;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's facet row.</summary>
internal static class ClassicHomeFacetRow
{
    static readonly MotionTokenDef s_fade =
        MotionTokenDef.Eased(Design.Motion.Faster, Easing.FluentStandard, ReducedMotionPolicy.KeepFade);

    /// <summary>The registered template of <see cref="ViewKeys.HomeFacetRow"/>.</summary>
    public static Element Template(FacetRowProps p, in ViewContext ctx)
    {
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
        Element row = Responsive.Of("home:facet", w => new BoxEl
        {
            Key = "home:facet:row",
            Direction = 0, AlignItems = FlexAlign.Center, Height = ClassicHomeRules.SelectorH, MinWidth = 0f,
            Children =
            [
                SelectorBar.Create(labels, p.Index(), onChange: OnWord,
                    style: FacetForm.IsTitle(w) ? R.Type.FacetTitleStyle : R.Type.FacetCompactStyle),
                new BoxEl { Grow = 1f, MinWidth = 0f },
                Flow.Show(() => hasSub, following),
            ],
        }, fallback: FacetForm.TitleMinWidth);

        // The busy bar (tuning F7): a FIXED 3-px slot, always laid out, so the band never moves; the stretched indeterminate bar
        // inside it only FADES in and out with Switch.Bar (a Show swap with an opacity Enter/Exit: a bound opacity would snap),
        // without re-rendering the row.
        var busy = new BoxEl
        {
            Key = "home:facet:bar",
            Height = ClassicHomeRules.BarH, MinWidth = 0f, HitTestVisible = false, Direction = 1,
            Children =
            [
                Flow.Show(() => p.Switch.Value.Bar, new BoxEl
                {
                    Key = "home:facet:bar:busy",
                    Height = ClassicHomeRules.BarH, MinWidth = 0f, HitTestVisible = false,
                    Enter = new EnterExit(Opacity: 0f, Active: true),
                    Exit = new EnterExit(Opacity: 0f, Active: true),
                    Transition = s_fade,
                    Children = [ProgressBar.Indeterminate(width: float.NaN)],
                }),
            ],
        };

        // The pinned band: selector row + bar, exactly FacetRowH tall, transparent. It pins against the PAGE column
        // (PageScope), which the page names — a scope-less Sticky would clamp to this row's own column.
        var band = new BoxEl
        {
            Key = "home:facet:band",
            Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Height = ClassicHomeRules.FacetRowH,
            Children = [row, busy],
        }.Sticky(ClassicHomeRules.StuckInset, scope: ClassicHomeRules.PageScope);

        return new BoxEl
        {
            Key = "home:facet",
            Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Children = [band],
        };
    }

    /// <summary>The failure InfoBar's layer (tuning H1): the page stacks it over the top of its content, so the bar has a slot of its own and
    /// the zones never move when it comes or goes.</summary>
    public static Element FailureLayer(FacetRowProps p) => Embed.Comp(() => new FailureLayerView(p)) with { Key = "home:facet:failure-layer" };

    /// <summary>Re-renders on the switch machine alone (the row's own slot does too), never the page.</summary>
    sealed class FailureLayerView(FacetRowProps p) : Component
    {
        public override Element Render()
        {
            var state = p.Switch.Value;
            string failedLabel = state.FailedTarget is { } ft ? LabelOf(p.Words.Value, ft) ?? ft : "";
            string failedTarget = state.FailedTarget ?? "";
            return new BoxEl
            {
                Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Justify = FlexJustify.Start, HitTestPassThrough = true,
                Children =
                [
                    Flow.Show(() => p.Switch.Value.Phase == FacetPhase.Failed, new BoxEl
                    {
                        Key = "home:facet:failure",
                        Direction = 1, MinWidth = 0f,
                        Enter = new EnterExit(Dy: ClassicHomeRules.FailureEnterDy, Opacity: 0f, Active: true),
                        Exit = new EnterExit(Opacity: 0f, Active: true),
                        Transition = MotionTokenDef.Eased(Design.Motion.Fast, Easing.FluentStandard, ReducedMotionPolicy.KeepFade),
                        Children =
                        [
                            InfoBar.Create(InfoBarSeverity.Error,
                                Strings.Home.CouldntLoad(failedLabel), Loc.Get(Strings.Home.CheckConnection),
                                onClose: p.Dismiss,
                                actionButton: Button.Create(Loc.Get(Strings.Common.Retry), () => p.Select(failedTarget), ButtonAppearance.Standard)),
                        ],
                    }),
                ],
            };
        }
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
