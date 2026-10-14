// ── Themes/Classic/Structure/Views/Home/ClassicHomeFacetRow.cs — Classic's `Home.FacetRow` (themes plan 6.1, T10a1) ─────────
//
// From `Home\Facet.UI.cs`: the facet row — SelectorBar (page-title style) + Following ToggleButton + busy ProgressBar + InfoBar. Every piece
// is a STOCK engine control — nothing hand-rolled. The facet words ARE the page title (there is no greeting headline), so the stock
// SelectorBar takes `R.Type.FacetTitleStyle` (28/36 display, rest secondary, selected primary semibold, no pill, a subtle hover plate,
// 40-px items) through its `style` parameter instead of its default 14-px pill look.
//
// ── The row (element tree) ───────────────────────────────────────────────────────────────────────────────────────────
//   column
//   ├─ band  (Sticky) column h=FacetRowH
//   │  ├─ HStack h=SelectorH(40) [ SelectorBar.Create(labels, Index, onChange, style: R.Type.FacetTitleStyle) · spacer
//   │  │                           · Flow.Show(hasSub, ToggleButton.Controlled) ]
//   │  └─ 3-px box  { ProgressBar.Indeterminate(width: NaN) }   Opacity ← Switch.Bar (compositor bind, no re-render)
//   └─ Flow.Show(Phase == Failed, InfoBar.Create(Error, "Couldn't load <facet>", …, Retry))
//
// The HStack + the 3-px bar form the BAND, exactly `FacetRowH` tall, pinned with `.Sticky(StuckInset, scope: PageScope)` (transparent, no
// acrylic — the page column names itself `PageScope`, so the pin holds for the whole page and not just this row's own column). The page
// cuts the content column under it with `.StickyClip(StuckBottom)` + a `WhileStuck` top feather (Browse.Page.cs's masthead idiom); the
// zones read the same constants for their chapter headers' stick inset. The InfoBar sits BELOW the band, unpinned — it scrolls with the
// page's flow and, when it grows, pushes the content (never overlays it).
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
        Element row = Responsive.Of(w => new BoxEl
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

        // The stretched indeterminate bar, always mounted: its opacity is a compositor bind on Switch.Bar so the fade
        // plays both ways without a re-render.
        var busy = new BoxEl
        {
            Key = "home:facet:bar",
            Height = ClassicHomeRules.BarH, MinWidth = 0f, HitTestVisible = false,
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
        });

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
