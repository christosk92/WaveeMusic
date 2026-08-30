using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The home facet row (Spotify <c>home.homeChips[]</c>): Music / Podcasts / Audiobooks, each optionally carrying
/// a second level ("Following").
///
/// An underline tab strip, not a row of pills. These facets select between whole VIEWS of the page, which is what a tab
/// strip is for and what a pill row is not: a pill reads as an additive filter you could have several of, and there is
/// only ever one facet.
///
/// <para>Hand-rolled rather than <see cref="SelectorBar"/> because the prototype's `.selbar` differs from that control in
/// four ways at once — the item is 600 weight and shifts from secondary to primary ink on selection, the indicator is a
/// full-width-minus-24 underline rather than a centred 16px pill, and the bar itself carries a bottom divider. The control
/// changes neither weight nor colour on selection and has no PartRoot to hang a divider from, so matching it would mean
/// fighting every one of those. The strip is ~20 lines; the control would be five workarounds.</para>
///
/// Selection writes <see cref="Services.HomeFacet"/> — an OPAQUE server token, never a synthesised or localised string —
/// and asks the page to refetch. The facet variable was always in the home request; it was simply hardcoded to "".</summary>
sealed class HomeFacetChips : Component
{
    internal sealed record Model(IReadOnlyList<HomeChip> Chips, Action OnFacetChanged);
    internal static readonly Context<Model?> Props = new(null);

    public override Element Render()
    {
        var model = UseContext(Props);
        var svc = UseContext(Services.Slot);
        if (model is null || svc is null || model.Chips.Count == 0) return new BoxEl();

        string? selected = svc.HomeFacet.Value;    // subscribe → the row re-renders on selection

        // Which top-level chip owns the current selection: either it IS the selection, or one of its sub-chips is. A
        // sub-selection keeps its PARENT tab underlined — the bar states which facet you are in, not which option.
        HomeChip? activeParent = null, activeSub = null;
        for (int i = 0; i < model.Chips.Count && activeParent is null; i++)
        {
            var chip = model.Chips[i];
            if (string.Equals(chip.Id, selected, StringComparison.Ordinal)) activeParent = chip;
            else
                foreach (var sub in chip.SubChips)
                    if (string.Equals(sub.Id, selected, StringComparison.Ordinal))
                    { activeParent = chip; activeSub = sub; break; }
        }

        var items = new List<Element>(model.Chips.Count + 3);
        // An "All" position the prototype does not have. Its selbar never models CLEARING a facet — every item is a
        // server chip and one is arbitrarily marked selected — so without this there is no way back to the unfiltered
        // feed once a chip is picked. A tab strip also needs exactly one selection to be a tab strip at all.
        items.Add(Tab(Loc.Get(Strings.Detail.Filter.All), activeParent is null, () => Select(svc, model, null)));
        foreach (var chip in model.Chips)
        {
            var c = chip;
            items.Add(Tab(c.Label, ReferenceEquals(c, activeParent), () => Select(svc, model, c.Id)));
        }

        // Sub-chips are the SECOND level: a subdued caption after a divider, exactly where the prototype puts
        // "Following" — one level down from the tabs rather than a peer of them. Still real controls, because "Following"
        // is a facet you select and a plain label could not be.
        var subs = activeParent?.SubChips ?? (IReadOnlyList<HomeChip>)Array.Empty<HomeChip>();
        if (subs.Count > 0)
        {
            items.Add(new BoxEl
            {
                Width = 1f, Height = Spacing.XXL, Shrink = 0f, Fill = Tok.StrokeDividerDefault,
                Margin = new Edges4(Spacing.S, 0f, 0f, 0f),
            });
            foreach (var child in subs)
            {
                var sub = child;
                bool on = ReferenceEquals(sub, activeSub);
                items.Add(new BoxEl
                {
                    Key = "facet-sub:" + sub.Id,
                    Shrink = 0f, Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS),
                    Corners = Radii.ControlAll,
                    Cursor = CursorId.Hand, Role = AutomationRole.Button,
                    // Re-picking the active option steps back to the bare parent facet — one step, not all the way to
                    // unfiltered, which is what the "All" tab is for.
                    OnClick = () => Select(svc, model, on ? activeParent!.Id : sub.Id),
                    Children =
                    [
                        Caption(sub.Label) with
                        {
                            MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                            Color = on ? Tok.TextPrimary : Tok.TextTertiary,
                            Weight = (ushort)(on ? 600 : 400),
                        },
                    ],
                    Animate = new LayoutTransition(TransitionChannels.Position | TransitionChannels.Opacity,
                        TransitionDynamics.Tween(220f, Easing.FluentAccelerate),
                        Exit: new EnterExit(Dx: -56f, Opacity: 0f, Active: true)),
                }.Interactive(Interaction.Subtle));
            }
        }

        // `.selbar { border-bottom: 1px solid divider }` — the rule the underlines sit on, which is what makes the strip
        // read as tabs rather than as a row of text buttons.
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinWidth = 0f, Wrap = true,
                    Children = [.. items],
                },
                new BoxEl { Height = 1f, Fill = Tok.StrokeDividerDefault },
            ],
        };
    }

    /// <summary>`.selitem` — 14/600, secondary ink going primary when selected, over a 3px accent underline inset 12px
    /// each side. The underline slot is ALWAYS reserved so selecting a tab cannot shift the strip's height.</summary>
    static Element Tab(string label, bool selected, Action onClick) => new BoxEl
    {
        Direction = 1, Shrink = 0f, AlignItems = FlexAlign.Stretch,
        Corners = new CornerRadius4(Radii.Control, Radii.Control, 0f, 0f),
        Cursor = CursorId.Hand, Role = AutomationRole.Tab,
        OnClick = onClick,
        Children =
        [
            new BoxEl
            {
                Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S),
                AlignItems = FlexAlign.Center,
                Children =
                [
                    BodyStrong(label) with
                    {
                        Color = selected ? Tok.TextPrimary : Tok.TextSecondary,
                        MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                    },
                ],
            },
            new BoxEl
            {
                // A 3-DIP indicator with a 2-DIP top corner: both are deliberately below their ramps' smallest rungs,
                // because a 4-DIP radius exceeds the bar's own height and a 4-DIP bar stops reading as an underline.
                Height = 3f, Margin = new Edges4(Spacing.M, 0f, Spacing.M, 0f),
                Corners = new CornerRadius4(2f, 2f, 0f, 0f),
                Fill = selected ? Tok.AccentDefault : ColorF.Transparent,
                BrushTransitionMs = MotionTok.ControlFast.DurationMs,
            },
        ],
    }.Interactive(Interaction.Subtle);

    // Writing the signal is the whole mutation; the page owns refetching, so this component never knows about Pathfinder
    // or caching. Peek-compare first so re-picking the current chip does not fire a redundant refresh.
    static void Select(Services svc, Model model, string? facetId)
    {
        if (string.Equals(svc.HomeFacet.Peek(), facetId, StringComparison.Ordinal)) return;
        svc.HomeFacet.Value = facetId;
        model.OnFacetChanged();
    }
}
