// ── Themes/WinUi/Structure/Views/Discovery/Home/WinUiHomeFacetRow.cs — WinUI's `Home.FacetRow` (themes plan 6.2; P6 W5) ───────────────
//
// Boards W-Home (the row under the greeting), W-Home2 (the same row pinned: a 48 bar with a hairline under it) and the tier boards. The facet words are WinUI's
// SelectorBar in its own voice: 14/20 words padded 12 in 40-tall items 4 apart, the selected word primary with the 3-px accent pill under it (the style's
// `FacetCompact` role: nothing here is a hand size). Following is a stock ToggleButton (32; 40 at Compact) with the people glyph. The busy ProgressBar and the
// failure InfoBar (`FailureLayer`) are the stock controls.
//
// ── The band (element tree) ───────────────────────────────────────────────────────────────────────────────────────────
//   column  Height = BandH (48)  ZStack  .Sticky(0, scope: PageScope, engaged: stuck)
//   ├─ HStack  padding (inset, 4, inset, 4)  h = 40  [ SelectorBar · spacer · Following (Standard, Huge) / the divider and Following (Compact) ]
//   ├─ 3-px slot at the foot { the busy ProgressBar, fading in and out }    (the slot never moves)
//   └─ the hairline at the foot, visible while the band is pinned (the `engaged` edge)
//
// The band is FULL WIDTH (the inset is its own padding): once it pins it reaches the card's left and right edges, as W-Home2 draws. It paints nothing of its own
// (the content under it cuts itself at the band's lower edge, `StickyClip(BandH)` on the page's content) except the 1-px hairline while stuck.
//
// THE FAILURE SLOT (tuning H1): a failed facet switch reverts to the published facet and says so in an InfoBar, in a layer the page stacks over the top edge of
// its content, so the zones never move when the bar comes or goes. The layer is pass-through: only the bar itself takes the pointer.
//
// Selection: the SelectorBar OWNS `Index` (the host's cell, written on click/roving before OnChange fires); the host re-syncs it from the switch machine, so a
// failed or no-op switch snaps the selection back. A word click selects the sub-chip when Following was last on for that word (`FollowingMemory`).

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
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

/// <summary>WinUI's facet row.</summary>
internal static class WinUiHomeFacetRow
{
    static readonly MotionTokenDef s_fade =
        MotionTokenDef.Eased(Design.Motion.Faster, Easing.FluentStandard, ReducedMotionPolicy.KeepFade);

    /// <summary>The registered template of <see cref="ViewKeys.HomeFacetRow"/>.</summary>
    public static Element Template(FacetRowProps p, in ViewContext ctx)
    {
        var words = p.Words.Value;
        var state = p.Switch.Value;
        var facts = ctx.Tier();
        var layout = WinUiHomeTiers.Of(facts);
        float inset = WinUiTiers.InsetOf(facts);
        // The band's pin edge: the hairline is on exactly while the band is pinned (written by the engine on the flip).
        var stuck = ctx.Hooks.UseRef(new Signal<bool>(false)).Value;

        var labels = new string[words.Length];
        for (int i = 0; i < labels.Length; i++) labels[i] = words[i].Label;
        var (idx, _) = FacetPivot.Resolve(words, state.Target);

        void OnWord(int i)
        {
            var ws = p.Words.Peek();
            if ((uint)i >= (uint)ws.Length) return;
            p.Select(FacetPivot.Target(ws[i], p.FollowingMemory(ws[i].Id)));
        }

        // The Following toggle stands at the row's end at Standard and Huge, after a hairline at Compact; at Small the page puts it on the greeting's row.
        bool following = layout.Following != WinUiFollowingPlace.GreetingRow;
        bool afterDivider = layout.Following == WinUiFollowingPlace.FacetRowAfterDivider;

        // The Friend activity button is Classic's Compact answer; WinUI's toolbar carries Friends (W2a), so the row ends with Following alone.
        // Compact: the words yield to the button (the board's `margin-right: -16px; overflow: hidden`): their box shrinks and clips, its trailing edge faded.
        bool yieldWords = afterDivider;
        var bar = SelectorBar.Create(labels, p.Index(), onChange: OnWord, style: R.Type.FacetCompactStyle with { ShowPill = true });
        Element row = new BoxEl
        {
            Key = "home:facet:row",
            Direction = 0, AlignItems = FlexAlign.Center, Height = WinUiHomeRules.SelectorH, MinWidth = 0f, Gap = Spacing.XS,
            Children =
            [
                new BoxEl
                {
                    Key = "home:facet:words",
                    Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f, Shrink = yieldWords ? 1f : 0f,
                    ClipToBounds = yieldWords,
                    EdgeFade = yieldWords ? new EdgeFadeSpec(EdgeMask.Right, WinUiHomeRules.DividerH + Spacing.S) : null,
                    Children = [bar],
                },
                new BoxEl { Grow = 1f, MinWidth = 0f },
                afterDivider
                    ? new BoxEl { Width = WinUiHomeRules.DividerW, Height = WinUiHomeRules.DividerH, Shrink = 0f, Margin = new Edges4(Spacing.XS, 0f, Spacing.XS, 0f), Fill = Tok.StrokeDividerDefault }
                    : new BoxEl(),
                following ? Following(p, layout) : new BoxEl(),
            ],
        };

        // The busy bar: a FIXED 3-px slot along the band's foot, always laid out, so the band never moves; the indeterminate bar inside it only FADES in and
        // out with Switch.Bar (a Show swap with an opacity Enter/Exit), without re-rendering the row.
        var busy = new BoxEl
        {
            Key = "home:facet:bar", AlignSelf = FlexAlign.End,
            Height = WinUiHomeRules.BarH, MinWidth = 0f, HitTestVisible = false, Direction = 1,
            Children =
            [
                Flow.Show(() => p.Switch.Value.Bar, new BoxEl
                {
                    Key = "home:facet:bar:busy",
                    Height = WinUiHomeRules.BarH, MinWidth = 0f, HitTestVisible = false,
                    Enter = new EnterExit(Opacity: 0f, Active: true),
                    Exit = new EnterExit(Opacity: 0f, Active: true),
                    Transition = s_fade,
                    Children = [ProgressBar.Indeterminate(width: float.NaN)],
                }),
            ],
        };

        // The pinned band's lower hairline: on while the band is pinned (the 1-px line W-Home2 draws under the bar).
        var line = new BoxEl
        {
            Key = "home:facet:line", AlignSelf = FlexAlign.End, Height = WinUiHomeRules.StuckLineH, MinWidth = 0f, HitTestVisible = false,
            Fill = Tok.StrokeDividerDefault, Opacity = Prop.Of(() => stuck.Value ? 1f : 0f),
            Transition = s_fade,
        };

        var band = new BoxEl
        {
            Key = "home:facet:band",
            Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Height = WinUiHomeRules.BandH, ZStack = true,
            Children =
            [
                new BoxEl
                {
                    Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch, Justify = FlexJustify.Center,
                    Padding = new Edges4(inset, 0f, inset, 0f),
                    Children = [row],
                },
                busy,
                line,
            ],
        }.Sticky(0f, scope: WinUiHomeRules.PageScope, engaged: stuck);

        return new BoxEl
        {
            Key = "home:facet",
            Direction = 1, MinWidth = 0f, AlignItems = FlexAlign.Stretch,
            Children = [band],
        };
    }

    /// <summary>The Following toggle: a stock ToggleButton with the people glyph, mounted only while there is a sub-chip to drive (a component, so the page's
    /// greeting row at Small reads the same machine without re-rendering the page). Value-controlled: the checked state is the switch machine's own
    /// resolution. Off returns to the selected word.</summary>
    public static Element Following(FacetRowProps p, WinUiHomeLayout layout)
        => new BoxEl { Shrink = 0f, Children = [Embed.Comp(() => new FollowingView(p, layout)) with { Key = "home:facet:following" }] };

    sealed class FollowingView(FacetRowProps p, WinUiHomeLayout layout) : Component
    {
        public override Element Render()
        {
            var words = p.Words.Value;
            var (idx, followingOn) = FacetPivot.Resolve(words, p.Switch.Value.Target);
            var word = (uint)idx < (uint)words.Length ? words[idx] : default;
            int followIdx = FacetPivot.FollowingWord(words, idx, true);
            if (followIdx < 0) return new BoxEl();
            var followWord = words[followIdx];
            return ToggleButton.Controlled(
                followWord.SubLabel ?? Loc.Get(Strings.Home.Following), followingOn,
                on => p.Select(on && followWord.SubId is not null ? followWord.SubId : word.Id),
                glyph: Icons.Friends,
                size: layout.FollowingH > 32f ? ControlSize.Large : ControlSize.Medium)
                with
                {
                    Enter = new EnterExit(Opacity: 0f, Active: true),
                    Exit = new EnterExit(Opacity: 0f, Active: true),
                    Transition = s_fade,
                };
        }
    }

    /// <summary>The failure InfoBar's layer (tuning H1): the page stacks it over the top of its content, so the bar has a slot of its own and the zones never
    /// move when it comes or goes.</summary>
    public static Element FailureLayer(FacetRowProps p) => Embed.Comp(() => new FailureLayerView(p)) with { Key = "home:facet:failure-layer" };

    /// <summary>Re-renders on the switch machine alone (the row's own slot does too), never the page.</summary>
    sealed class FailureLayerView(FacetRowProps p) : Component
    {
        public override Element Render()
        {
            // The tier's bar: 72 tall with a 40 Retry at Compact (the touch floor), the stock bar elsewhere. A tier crossing re-renders only this leaf.
            var themeFacts = UseContext(ThemeFactsContext.Signal);
            var layout = WinUiHomeTiers.Of(themeFacts is null ? TierFacts.Unknown : themeFacts.Value.Tiers);
            TemplateParts? barParts = null;
            if (layout.FailureBarH > 0f)
            {
                barParts = new TemplateParts();
                float barH = layout.FailureBarH;
                barParts.Set<BoxEl>(InfoBar.PartRoot, b => b with { MinHeight = barH });
            }
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
                        Enter = new EnterExit(Dy: WinUiHomeRules.FailureEnterDy, Opacity: 0f, Active: true),
                        Exit = new EnterExit(Opacity: 0f, Active: true),
                        Transition = MotionTokenDef.Eased(Design.Motion.Fast, Easing.FluentStandard, ReducedMotionPolicy.KeepFade),
                        Children =
                        [
                            InfoBar.Create(InfoBarSeverity.Error,
                                Strings.Home.CouldntLoad(failedLabel), Loc.Get(Strings.Home.CheckConnection),
                                onClose: p.Dismiss,
                                actionButton: Button.Create(Loc.Get(Strings.Common.Retry), () => p.Select(failedTarget), ButtonAppearance.Standard, layout.FailureRetry),
                                parts: barParts),
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
