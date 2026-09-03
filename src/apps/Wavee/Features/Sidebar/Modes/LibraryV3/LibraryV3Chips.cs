using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Scroll;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>
/// §3.2.4 — the library filter rail: Playlists · Podcasts · Albums · Artists, where the SELECTED facet carries its own
/// sub-filter (the playlist qualifier: By you · By Spotify · Mixed) instead of a second rail beneath it.
///
/// <para>THE GRAMMAR (the chrome sweep's rewrite of this file — <c>library-v3-chrome-implementation.md</c> W2). Three
/// shapes, driven by the pure <see cref="LibraryV3ChipStrip"/> model instead of hand-rolled branching in the
/// renderer:</para>
/// <list type="number">
/// <item><b>idle</b> — the four facets at rest, nothing else. No leading ✕: there is nothing yet to clear;</item>
/// <item><b>filtered</b> — picking a facet pops a leading round ✕ IN (scale + fade) and the OTHER three facets slide
///   OUT (the "other kinds leave" read Spotify uses); if the facet owns a sub-filter the data actually evidences
///   (Playlists' By you/By Spotify/Mixed), its three options spill in right after it;</item>
/// <item><b>fused</b> — picking one of those options FUSES it into the facet's own pill via <c>ConcertUi.SegmentedPill</c>
///   (the SIDEBAR register, <c>ConcertUi.cs</c>): <c>[✓ Playlists │ By you ✕]</c>. Tapping the fused pill is one step
///   BACK (drop the qualifier, keep the facet + its re-spilled options) — tapping ✕ clears everything to idle.</item>
/// </list>
///
/// <para>THE FUSED PILL IS THE SHARED PRIMITIVE, not a hand-rolled shape: <c>ConcertUi.SegmentedPill</c> is the exact
/// same factory the Concerts filter bar and the Home tab strip use, at a new <c>SegmentedPillStyle.Sidebar</c> register
/// (28-DIP scale, opaque <c>FillControlSolid</c> segment, <c>Tok.OnAccent</c> ink, an X because tapping clears rather
/// than reopening a menu). Hand-rolling it a fourth time is exactly the defect this rewrite deletes — the old fused
/// pill used the token combination <c>ConcertUi.cs</c> itself documents as broken (a translucent "card" fill under
/// accent-on-neutral ink reads as cyan-on-cyan) and a hard-coded chevron for an action that clears.</para>
///
/// <para>WHY A PURE SLOT MODEL (<see cref="LibraryV3ChipStrip"/>, engine-free, unit-tested): "which slots exist, in
/// which order, and what a tap on each one writes" is exactly the kind of decision that used to live in this
/// component's branching and could only be checked by eye. It is now a <c>List&lt;V3ChipSlot&gt;</c> — a pure function
/// of (filter, qualifier, qualifiersAvailable) — so idle/filtered/fused, the Clear/Facet/Fused/Option write targets and
/// the loose-pill ⇄ fused-pill shared key are all pinned in <c>LibraryV3ChipStripTests</c> without a window.</para>
///
/// <para>WHY ONE <c>Apply</c> METHOD: every slot already carries what a tap on it should write
/// (<see cref="V3ChipSlot.SelectFilter"/>/<see cref="V3ChipSlot.SelectQualifier"/>) except the Clear slot, whose
/// gesture is a full reset (filter, qualifier AND the search text AND the drill stack) — so Clear alone routes to
/// <see cref="LibraryV3Session.ClearAllFilters"/> and every other kind routes through <see cref="Apply"/>. Mouse and
/// keyboard (Space/Enter on the roved chip) share this exact path, so there is no way for a click and an arrow-key
/// activation of the "same" chip to disagree.</para>
///
/// <para>Two decisions worth not re-litigating. (1) The four kind pills are ALWAYS shown at idle: they are the app's
/// own fixed taxonomy, and hiding one because the library has not warmed yet makes the filter set look unstable — a
/// kind with zero entries still filters, and the honest result is the "empty by filter" state. (2) The qualifier is
/// the opposite: it exists only when the data actually distinguishes ≥2 known provenance classes
/// (<c>Entries.QualifiersAvailable</c>, <c>SidebarProjection.QualifiersAvailable</c>'s ≥2-flavor rule), so an
/// unevidenced qualifier is not offered and the selected Playlists facet simply never fuses. A persisted qualifier
/// whose precondition stops holding is CLEARED here — a filter you cannot see must never keep filtering.</para>
///
/// <para>The rail is ONE tab stop with roving focus (the <c>RadioButtons</c> pattern) over whatever is currently on
/// it. Focus is tracked by the slot's KEY, not its index (<see cref="LibraryV3ChipStrip.FocusIndex"/>) — an index
/// would point at the wrong chip the instant the rail's shape changes (a facet spills options, a qualifier fuses);
/// a key survives because the shared-key morph keeps naming the same logical chip across those transitions.
/// Left/Right move and re-place the focus visual, Home/End jump, Space/Enter activate, Tab leaves.
/// Every filter or qualifier change homes the rail (<c>ScrollController.ScrollTo(0)</c>): the ✕ and the selected facet lead it.
/// </para>
/// </summary>
sealed class LibraryV3Chips : Component
{
    // ── motion ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The ✕'s own pop-scale in and out. It has no sibling to reuse a key with (unlike every other slot,
    /// which morphs into or out of another shape) — its mount/unmount IS the motion.</summary>
    static readonly LayoutTransition ClearMotion = new(
        TransitionChannels.Position | TransitionChannels.Opacity,
        TransitionDynamics.Tween(WaveeMotion.Fast, Easing.SmoothOut),
        Enter: new EnterExit(Sx: 0.6f, Sy: 0.6f, Opacity: 0f, Active: true),
        Exit: new EnterExit(Sx: 0.6f, Sy: 0.6f, Opacity: 0f, Active: true));

    /// <summary>A top-level facet's FLIP + fade. Only an EXIT leg (toward the ✕, leftward): a facet returning on
    /// Clear reappears at its idle position with no entrance of its own to play — the incoming ✕ and the remaining
    /// facet's own reflow already read as motion enough.</summary>
    static readonly LayoutTransition FacetMotion = new(
        TransitionChannels.Position | TransitionChannels.Opacity,
        TransitionDynamics.Tween(220f, Easing.SmoothOut),
        Exit: new EnterExit(Dx: -12f, Opacity: 0f, Active: true));

    /// <summary>A spilled qualifier option: it enters sliding in from the facet's side (+12, "spilling out of the
    /// pill") and, when picked, exits toward the pill at −56 — the same distance <c>ConcertUi</c>'s segment-dock leg
    /// travels — so the option visually flies INTO the fused pill it is about to become the value of.</summary>
    static readonly LayoutTransition OptionMotion = new(
        TransitionChannels.Position | TransitionChannels.Opacity,
        TransitionDynamics.Tween(220f, Easing.FluentAccelerate),
        Enter: new EnterExit(Dx: 12f, Opacity: 0f, Active: true),
        Exit: new EnterExit(Dx: -56f, Opacity: 0f, Active: true));

    readonly LibraryV3Session _session;

    public LibraryV3Chips(LibraryV3Session session) => _session = session;

    public override Element Render()
    {
        var prefs = UseContext(SidebarPreferences.Slot);
        var hooks = UseContext(InputHooks.Current);
        var focusedKey = UseSignal<string?>(null);
        var nodes = UseMemo(static () => new Dictionary<string, NodeHandle>(8), DepKey.Empty);
        // A reusable scratch buffer for the prune pass below — never a signal, this is a buffer, not state.
        var stale = UseMemo(static () => new List<string>(4), DepKey.Empty);
        var controller = UseMemo(static () => new ScrollController(), DepKey.Empty);

        int filter = prefs is null ? 0 : LibraryV3Metrics.NormalizeFilter(prefs.V3Filter.Value);
        int qualifier = prefs is null ? 0 : LibraryV3Metrics.NormalizeQualifier(prefs.V3Qualifier.Value);
        if (prefs is not null) _ = prefs.Entries.Version.Value;   // subscribe: QualifiersAvailable moves with the projection
        bool qualifiersAvailable = prefs?.Entries.QualifiersAvailable ?? false;
        bool qualifierRelevant = filter == (int)SidebarV3Filter.Playlists && qualifiersAvailable;

        // Auto-correction (§3.2.4 / §3.2.17). In an EFFECT, never in the render body: a preference write during
        // render would be a render-purity violation and would re-enter this component mid-flush.
        UseLayoutEffect(() =>
        {
            if (prefs is not { } pf) return;
            if (!qualifierRelevant && pf.V3Qualifier.Peek() != (int)SidebarV3Qualifier.Any)
                pf.SetV3Qualifier((int)SidebarV3Qualifier.Any);
        }, DepKey.From(qualifierRelevant ? 1 : 0, qualifier));

        if (prefs is not { } p) return new BoxEl();

        var slots = LibraryV3ChipStrip.Slots(filter, qualifier, qualifiersAvailable);
        int focusIdx = LibraryV3ChipStrip.FocusIndex(slots, focusedKey.Value);

        // Prune handles for keys THIS render no longer emits (a folded-back option, a facet that fused). NOT a blind
        // Clear(): OnRealized fires ONLY AT MOUNT (Element.cs), so a key that survives this render under the SAME
        // identity — the shared "v3f{code}" a facet and its fused pill trade places under — never re-invokes it, and
        // the scroll-into-view effect just below needs exactly that handle on the render where the key first moves.
        stale.Clear();
        foreach (var key in nodes.Keys)
        {
            bool live = false;
            foreach (var s in slots) if (s.Key == key) { live = true; break; }
            if (!live) stale.Add(key);
        }
        foreach (var key in stale) nodes.Remove(key);

        // HOME the rail on every filter/qualifier change: the ✕ and the selected facet are always the rail's FIRST
        // slots, so offset 0 is where the new shape lives. (Bringing the selected pill to the leading edge instead
        // scrolled the ✕ — and the lane padding — out of view the moment a filter was picked.) Keyed on the two
        // persisted values so a rebuild for any other reason (a library refresh) never re-triggers a scroll.
        UseLayoutEffect(() => controller.ScrollTo(0f), DepKey.From(filter, qualifier));

        var children = new List<Element>(slots.Count);   // the a11y group label is mounted by Rail(), outside the gapped row
        for (int i = 0; i < slots.Count; i++)
            children.Add(Chip(nodes, p, slots[i], qualifier, i == focusIdx));

        return Rail(children, controller, e => Rove(e, hooks, nodes, focusedKey, slots, s => Apply(p, s)));
    }

    // ── selection ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What ANY non-Clear chip tap — or its keyboard equivalent — writes. The slot already carries the
    /// answer, so a selected facet's toggle-off, a fused pill's step-back and an option's pick are ONE code path.
    /// </summary>
    void Apply(SidebarPreferences prefs, V3ChipSlot slot)
    {
        if (slot.Kind == V3ChipKind.Clear) { _session.ClearAllFilters(); return; }
        if (slot.SelectFilter != LibraryV3Metrics.NormalizeFilter(prefs.V3Filter.Peek()))
            SelectFilter(prefs, slot.SelectFilter);
        prefs.SetV3Qualifier(slot.SelectQualifier);
    }

    /// <summary>Changing the kind filter clears the qualifier whenever the new kind is not Playlists, drops a Custom
    /// sort that only exists under Playlists (§3.2.6's fallback, persisted), and leaves any drill-in level — the
    /// folder you were inside may not even be part of the new kind set.</summary>
    void SelectFilter(SidebarPreferences prefs, int code)
    {
        prefs.SetV3Filter(code);
        if (code != (int)SidebarV3Filter.Playlists)
        {
            prefs.SetV3Qualifier((int)SidebarV3Qualifier.Any);
            if (prefs.V3Sort.Peek() == (int)SidebarV3Sort.Custom)
                prefs.SetV3Sort((int)SidebarV3Sort.Recents, false);
        }
        _session.ResetDrill();
    }

    // ── chrome ────────────────────────────────────────────────────────────────────────────────────────────────────

    Element Chip(Dictionary<string, NodeHandle> nodes, SidebarPreferences prefs, V3ChipSlot slot, int qualifier,
                bool focusable)
        => slot.Kind switch
        {
            V3ChipKind.Clear => ClearPill(nodes, focusable, _session.ClearAllFilters),
            V3ChipKind.Fused => FusedPill(nodes, slot, qualifier, focusable, () => Apply(prefs, slot)),
            V3ChipKind.Option => Pill(nodes, slot, LibraryV3Labels.Qualifier(slot.Code), fontSize: 12f, focusable,
                                       OptionMotion, () => Apply(prefs, slot)),
            // Issue #85 (H4) — a PLAIN tap still only writes the filter (Apply); a DOUBLE-CLICK on a facet that has
            // an actual page (Albums/Artists/Podcasts — Playlists' Route is null) navigates instead, the same
            // "double click always activates plainly" convention the tree rows use
            // (SidebarEntityRow.OnPointerReleased). Keyboard Space/Enter (Rove, below) always filters — the
            // destinations stay reachable from the top bar for a keyboard-only user.
            _ => Pill(nodes, slot, LibraryV3Labels.Filter(slot.Code), fontSize: 13f, focusable, FacetMotion,
                      () => Apply(prefs, slot),
                      onNavigate: slot.Route is { Length: > 0 } r
                          ? () => _session.Go(r, LibraryV3Labels.Filter(slot.Code))
                          : null),
        };

    static Element Rail(List<Element> chips, ScrollController controller, Action<KeyEventArgs> onKey)
        => ScrollView(new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, MinWidth = 0f,
            // The LEAD lane (W7), not the full band inset: the first chip's leading edge lands on the art column
            // every row shares, not on the head's own left padding.
            Padding = SidebarPaneMetrics.LeadBandInset,
            OnKeyDown = onKey,
            // The zero-size a11y label sits OUTSIDE the gapped chip row: as a flex sibling of the chips it collected
            // the row's 6-DIP Gap after itself and pushed the first pill 6 DIP off the art column.
            Children =
            [
                GroupLabel(),
                new BoxEl { Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center, MinWidth = 0f, Children = [.. chips] },
            ],
        }, horizontal: true) with
        {
            Grow = 0f, Height = LibraryV3Metrics.ChipRailHeight, AutoEdgeFade = true, SuppressScrollBar = true,
            ScrollKey = "sidebar.v3.chips", Controller = controller,
        };

    /// <summary>The rail's a11y group label, painted at ZERO SIZE (not merely zero opacity) so it adds nothing to
    /// the scroller's content extent — the engine has no automation-name channel and no RadioButtons container role,
    /// so text is the only place a group label can live, and it must not cost the rail a phantom scroll inch.
    /// </summary>
    static Element GroupLabel() => new BoxEl
    {
        Key = "v3-filter-group",
        Width = 0f, Height = 0f, ClipToBounds = true, HitTestVisible = false,
        Children = [new TextEl(Loc.Get(Strings.Sidebar.A11y.FilterGroup)) { Size = 1f, MaxLines = 1 }],
    };

    /// <summary>The leading ✕: clears everything (filter, qualifier, search text, drill) in one gesture. Built on
    /// <c>Interaction.Control</c> rather than the app's usual <c>Interaction.Subtle</c> — its resting state must
    /// itself be a visible bordered surface (<c>FillControlDefault</c> + <c>StrokeControlDefault</c>), not the
    /// invisible-until-hovered ramp <c>Subtle</c> gives chrome affordances that already have a border or an icon
    /// doing the work; the ✕ has neither until you look for it, so it needs to read as a control at rest.</summary>
    static Element ClearPill(Dictionary<string, NodeHandle> nodes, bool focusable, Action onClick) => ToolTip.Wrap(
        new BoxEl
        {
            Key = "v3-clear",
            Animate = ClearMotion,
            Width = 28f, Height = 28f, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.FullAll,
            Role = AutomationRole.Button, Cursor = CursorId.Hand,
            Focusable = focusable, FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
            OnClick = onClick,
            OnRealized = h => nodes["v3-clear"] = h,
            Children = [Icon(Icons.Cancel, 12f, Tok.TextPrimary)],
        }.Interactive(Interaction.Control),
        Loc.Get(Strings.Sidebar.V3.ClearFilters));

    /// <summary>A facet or a spilled qualifier option — the SAME padding whether selected or not, so a facet's width
    /// never changes at select time (the defect this rewrite deletes: a check glyph that shoved the label through a
    /// 220 ms reflow). Selection is COLOUR only — accent fill + border + <c>Tok.OnAccent</c> ink, cross-fading in
    /// <c>BrushTransitionMs</c> — so <paramref name="animate"/> only ever carries the FLIP, never a size change.
    /// </summary>
    static Element Pill(Dictionary<string, NodeHandle> nodes, V3ChipSlot slot, string label, float fontSize,
                        bool focusable, LayoutTransition animate, Action onClick, Action? onNavigate = null)
    {
        // Caption, not Body: the rail's pills read at 13/12 pt, under Body's 14 pt reading size, and Caption is
        // already the alias tuned for sub-Body chrome text — Body would need an explicit Wrap/MaxLines override
        // just to behave as a one-line chip label.
        var text = Caption(label) with
        {
            Size = fontSize, Weight = (ushort)(slot.Selected ? 600 : 400),
            Color = slot.Selected ? Tok.OnAccent : Tok.TextPrimary, MaxLines = 1,
        };
        return new BoxEl
        {
            // KEYED, and the fused pill reuses a facet's key ("v3f{code}") — that shared identity is the whole morph.
            Key = slot.Key,
            Animate = animate,
            Direction = 0, Height = 28f, Shrink = 0f, AlignItems = FlexAlign.Center,
            Padding = new Edges4(12f, 0f, 12f, 0f),
            Corners = Radii.FullAll,
            Fill = slot.Selected ? Tok.AccentDefault : Tok.FillControlDefault,
            HoverFill = slot.Selected ? Tok.AccentSecondary : Tok.FillControlSecondary,
            PressedFill = slot.Selected ? Tok.AccentTertiary : Tok.FillControlTertiary,
            BorderWidth = 1f, BorderColor = slot.Selected ? Tok.AccentDefault : Tok.StrokeControlDefault,
            BrushTransitionMs = WaveeMotion.Fast,
            Role = AutomationRole.RadioButton, Cursor = CursorId.Hand,
            Focusable = focusable, FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
            // A route-bearing chip trades OnClick for OnPointerReleased so a double-click can be told apart from a
            // plain tap (args.ClickCount) — every other chip keeps the plain OnClick it always had.
            OnClick = onNavigate is null ? onClick : null,
            OnPointerReleased = onNavigate is null ? null : args =>
            {
                if (args.ClickCount >= 2) onNavigate();
                else onClick();
            },
            OnRealized = h => nodes[slot.Key] = h,
            Children = [text],
        };
    }

    /// <summary>The FUSED facet pill — <c>ConcertUi.SegmentedPill</c> at the <c>Sidebar</c> register, the exact same
    /// factory the Concerts filter bar and the Home tab strip use. The factory hard-codes <c>Focusable = true</c> /
    /// <c>Role = Button</c> for its own (single-pill) call sites, so this override is REQUIRED: the rail is one tab
    /// stop with roving focus over several chips, and the fused slot is a radio position among them, not its own
    /// stop.</summary>
    static Element FusedPill(Dictionary<string, NodeHandle> nodes, V3ChipSlot slot, int qualifier, bool focusable,
                            Action onClick) => ConcertUi.SegmentedPill(
                slot.Key, SegmentedPillStyle.Sidebar, LibraryV3Labels.Filter(slot.Code),
                LibraryV3Labels.Qualifier(qualifier), onClick) with
    {
        Focusable = focusable, OnRealized = h => nodes[slot.Key] = h, Role = AutomationRole.RadioButton,
    };

    /// <summary>Roving focus for the rail. Selection does NOT follow focus here (unlike <c>RadioButtons</c>): a
    /// filter is a data operation, so arrowing across the rail must not fire a projection per keystroke — Space/Enter
    /// commits. The slot list is whatever is currently ON the rail, so spilled options and a fused pill join the
    /// same single tab stop, and the focused KEY (not an index) is what survives a relayout.</summary>
    static void Rove(KeyEventArgs e, InputHooks hooks, Dictionary<string, NodeHandle> nodes, Signal<string?> focusedKey,
                     List<V3ChipSlot> slots, Action<V3ChipSlot> activate)
    {
        if (e.Handled) return;
        int n = slots.Count;
        if (n == 0) return;
        int cur = LibraryV3ChipStrip.FocusIndex(slots, focusedKey.Peek());
        int next;
        switch (e.KeyCode)
        {
            case Keys.Left: next = cur == 0 ? n - 1 : cur - 1; break;
            case Keys.Right: next = cur == n - 1 ? 0 : cur + 1; break;
            case Keys.Home: next = 0; break;
            case Keys.End: next = n - 1; break;
            case Keys.Space:
            case Keys.Enter:
                activate(slots[cur]);
                e.Handled = true;
                return;
            default:
                return;
        }
        focusedKey.Value = slots[next].Key;
        if (nodes.TryGetValue(slots[next].Key, out var h) && !h.IsNull)
            (hooks.MoveFocusVisual ?? hooks.RestoreFocus)?.Invoke(h);
        e.Handled = true;
    }
}
