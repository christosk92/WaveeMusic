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
/// fighting every one of those.</para>
///
/// <para>The SECOND level speaks the Concerts / LibraryV3 grammar rather than being appended after the last tab behind a
/// divider: a selected parent spills its sub-chips inline INSIDE its own group, and selecting one fuses the pair into a
/// strip-register <see cref="ConcertUi.SegmentedPill"/> that occupies the parent's slot with the underline still lit.
/// Tapping the fused pill steps back ONE level (to the bare parent facet), never all the way to unfiltered — that is what
/// "All" is for. The morph is a shared node KEY: the tab's label box and the fused pill are the same key inside the same
/// tab shell, so the reconciler reuses the node and its width reflows instead of popping. That is also why every parent's
/// group node is ALWAYS present, subs or not — a re-parent on the very transition would kill the morph. The ORDERING
/// rules live in <see cref="HomeFacetStrip"/>, engine-free and unit-tested; this file owns only the pixels.</para>
///
/// Selection writes <see cref="Services.HomeFacet"/> — an OPAQUE server token, never a synthesised or localised string —
/// and hands the page the (previous, next) pair, so it can refetch and put the strip back if that fetch fails.</summary>
sealed class HomeFacetChips : Component
{
    internal sealed record Model(IReadOnlyList<HomeChip> Chips, Action<string?, string?> OnFacetChanged);
    internal static readonly Context<Model?> Props = new(null);

    public override Element Render()
    {
        var model = UseContext(Props);
        var svc = UseContext(Services.Slot);
        if (model is null || svc is null || model.Chips.Count == 0) return new BoxEl();

        string? selected = svc.HomeFacet.Value;    // subscribe → the row re-renders on selection
        var slots = HomeFacetStrip.Slots(model.Chips, selected);

        var items = new List<Element>(slots.Count);
        HomeChip? group = null;
        var groupItems = new List<Element>(3);
        // A parent tab and the subs it spilled are ONE group node, keyed by the parent's id. The group is the stable
        // parent across the fuse, so label → pill is a keyed swap of a child rather than a re-parent.
        void Flush()
        {
            if (group is not { } parent) return;
            items.Add(new BoxEl
            {
                Key = "facet-group:" + parent.Id,
                Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.XS, Shrink = 0f,
                Children = [.. groupItems],
            });
            group = null;
            groupItems.Clear();
        }

        foreach (var slot in slots)
        {
            switch (slot.Kind)
            {
                // An "All" position the prototype does not have. Its selbar never models CLEARING a facet — every item
                // is a server chip and one is arbitrarily marked selected — so without this there is no way back to the
                // unfiltered feed once a chip is picked. A tab strip also needs exactly one selection to be one at all.
                case FacetSlotKind.All:
                    items.Add(Tab(slot.Key, Loc.Get(Strings.Detail.Filter.All), slot.Selected,
                        () => Select(svc, model, null)));
                    break;
                case FacetSlotKind.Tab:
                    Flush();
                    group = slot.Chip;
                    groupItems.Add(Tab(slot.Key, slot.Chip!.Label, slot.Selected,
                        () => Select(svc, model, slot.Select)));
                    break;
                case FacetSlotKind.Fused:
                    Flush();
                    group = slot.Chip;
                    groupItems.Add(FusedTab(slot.Key, slot.Chip!.Label, slot.Sub!.Label,
                        () => Select(svc, model, slot.Select)));
                    break;
                case FacetSlotKind.Sub:
                    groupItems.Add(SubToken(slot.Key, slot.Sub!.Label, () => Select(svc, model, slot.Select)));
                    break;
            }
        }
        Flush();

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
    /// each side. The underline slot is ALWAYS reserved so selecting a tab cannot shift the strip's height.
    /// <para><paramref name="key"/> rides the LABEL box, not the tab shell: that box and the fused
    /// <see cref="ConcertUi.SegmentedPill"/> are the two shapes of ONE node, so the reconciler reuses it across the
    /// fuse. It carries the width-reflow recipe as the LOOSE half of that morph; the pill carries the other half.</para></summary>
    static Element Tab(string key, string label, bool selected, Action onClick) => new BoxEl
    {
        Direction = 1, Shrink = 0f, AlignItems = FlexAlign.Stretch,
        Corners = new CornerRadius4(Radii.Control, Radii.Control, 0f, 0f),
        Cursor = CursorId.Hand, Role = AutomationRole.Tab,
        OnClick = onClick,
        Children =
        [
            new BoxEl
            {
                Key = key,
                Animate = new LayoutTransition(
                    TransitionChannels.Position | TransitionChannels.Size,
                    TransitionDynamics.Tween(260f, Easing.SmoothOut),
                    Size: SizeMode.Reflow, Axes: SizeAxes.Width),
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
            Underline(selected),
        ],
    }.Interactive(Interaction.Subtle);

    /// <summary>The same tab shell with its underline lit, except the label slot holds the fused pill
    /// (<c>Music | Following</c>) under the tab's own <paramref name="key"/>, so the loose label morphs into it in place.
    /// <para>The shell is inert here: the pill IS the control (its own Role / Focusable / Cursor and its own hover and
    /// pressed fills), so a second handler on the shell would double-fire the step back.</para></summary>
    static Element FusedTab(string key, string parentLabel, string subLabel, Action onClick) => new BoxEl
    {
        Direction = 1, Shrink = 0f, AlignItems = FlexAlign.Stretch,
        Corners = new CornerRadius4(Radii.Control, Radii.Control, 0f, 0f),
        Children =
        [
            ConcertUi.SegmentedPill(key, SegmentedPillStyle.Strip, parentLabel, subLabel, onClick),
            Underline(true),
        ],
    };

    /// <summary>A 3-DIP indicator with a 2-DIP top corner: both are deliberately below their ramps' smallest rungs,
    /// because a 4-DIP radius exceeds the bar's own height and a 4-DIP bar stops reading as an underline.</summary>
    static Element Underline(bool selected) => new BoxEl
    {
        Height = 3f, Margin = new Edges4(Spacing.M, 0f, Spacing.M, 0f),
        Corners = new CornerRadius4(2f, 2f, 0f, 0f),
        Fill = selected ? Tok.AccentDefault : ColorF.Transparent,
        BrushTransitionMs = MotionTok.ControlFast.DurationMs,
    };

    /// <summary>A second-level option, spilled inline inside its parent's group: a subdued caption one level down from
    /// the tabs rather than a peer of them, which is exactly where the prototype puts "Following". Still a real control,
    /// because "Following" is a facet you select and a plain label could not be. It only ever renders UNSELECTED —
    /// selecting it is what turns the parent into a <see cref="FusedTab"/> — so its exit flies −56 DIP toward the pill
    /// it is becoming, meeting the segment that enters from the opposite side.</summary>
    static Element SubToken(string key, string label, Action onClick) => new BoxEl
    {
        Key = key,
        Shrink = 0f, Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS),
        Corners = Radii.ControlAll,
        Cursor = CursorId.Hand, Role = AutomationRole.Button,
        OnClick = onClick,
        Children =
        [
            Caption(label) with
            {
                MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                Color = Tok.TextTertiary,
            },
        ],
        Animate = new LayoutTransition(TransitionChannels.Position | TransitionChannels.Opacity,
            TransitionDynamics.Tween(220f, Easing.FluentAccelerate),
            Exit: new EnterExit(Dx: -56f, Opacity: 0f, Active: true)),
    }.Interactive(Interaction.Subtle);

    // Writing the signal is the whole mutation; the page owns refetching, so this component never knows about Pathfinder
    // or caching. Peek-compare first so re-picking the current chip does not fire a redundant refresh — and hand the
    // PREVIOUS facet along, because a failed fetch has to put the strip back where it was.
    static void Select(Services svc, Model model, string? facetId)
    {
        string? previous = svc.HomeFacet.Peek();
        if (string.Equals(previous, facetId, StringComparison.Ordinal)) return;
        svc.HomeFacet.Value = facetId;
        model.OnFacetChanged(previous, facetId);
    }
}
