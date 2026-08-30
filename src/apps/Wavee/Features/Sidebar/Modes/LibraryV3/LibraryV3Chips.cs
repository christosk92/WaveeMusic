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
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>
/// §3.2.4 — the library filter rail: Playlists · Podcasts · Albums · Artists, where the SELECTED facet carries its own
/// sub-filter (the playlist qualifier: By you · By Spotify · Mixed) instead of a second rail beneath it.
///
/// <para>ONE ROW, THE MORPHING FACET GRAMMAR. This surface used to stack a primary chip rail over a conditional qualifier
/// rail — two bands, 68 DIP of chrome, and a qualifier that read as a peer of the kinds rather than as a property of
/// Playlists. It now speaks the app's existing facet-chip language, copied from <c>HomeFacetChips</c> (which in turn is
/// the Concerts filter-bar grammar in <c>ConcertUi.FilterToken</c>/<c>ConcertUi.SegmentedPill</c>):</para>
/// <list type="number">
/// <item>picking a facet CHECKS its pill (accent fill + check mark) and, if that facet owns a sub-filter, SPILLS the
///   sub-options inline right after it — inside the facet's own tight group, so they read as its children;</item>
/// <item>picking one of those options FUSES it into the pill: the pill becomes a compound accent capsule
///   <c>[✓ Playlists │ By you ▾]</c> — a raised inner segment carrying the facet, then the value, then a chevron;</item>
/// <item>tapping the fused pill drops the sub-filter and re-spills the options (one step back, not all the way);
///   tapping the plain checked pill clears the facet back to All. Both directions are the same reverse morph.</item>
/// </list>
///
/// <para>THE FUSION MECHANISM IS A SHARED KEY, and it is the only reason this morphs instead of popping: the loose pill and
/// the fused pill are emitted under the SAME key (<c>"v3f{code}"</c>) inside the SAME always-present group node, so the
/// reconciler REUSES that node across the swap and the width-reflow recipe both shapes carry has a previous width to
/// animate from. Distinct keys (or a group that only exists while options are spilled) unmount one pill and mount
/// another, and there is nothing left to reflow. The motion recipes below are <c>ConcertUi</c>'s verbatim — 220 ms
/// width reflow on the loose pill, 260 ms on the fused one, the 300 ms segment dock, the 220 ms
/// <c>FluentAccelerate</c> option fly — so the sidebar and the homepage feel like one control. Only the SCALE is the
/// sidebar's (28-DIP pills, 13/12 pt text, the pane's <c>FillSubtle*</c> ramp): a 240–320 DIP rail cannot carry the
/// page's 32-DIP bordered tokens.</para>
///
/// <para>Two decisions worth not re-litigating. (1) The four kind pills are ALWAYS shown: they are the app's own fixed
/// taxonomy, and hiding one because the library has not warmed yet makes the filter set look unstable — a kind with zero
/// entries still filters, and the honest result is the "empty by filter" state. (2) The qualifier is the opposite: it
/// exists only when the data actually distinguishes ≥2 known provenance classes (<c>Entries.QualifiersAvailable</c>,
/// which is <c>SidebarProjection.QualifiersAvailable</c>'s ≥2-flavor rule), so an unevidenced qualifier is not offered
/// and the checked Playlists pill simply never fuses. A persisted qualifier whose precondition stops holding is CLEARED
/// here — a filter you cannot see must never keep filtering.</para>
///
/// <para>The rail is ONE tab stop with roving focus (the <c>RadioButtons</c> pattern) over whatever is currently on it —
/// the four pills, plus the spilled options while they are up. Left/Right move the index and re-place the focus visual,
/// Home/End jump, Space/Enter activate, Tab leaves. Seven chips must not cost seven of the pane's nine tab stops
/// (§3.2.12). There is no leading clear-X: the checked pill IS the toggle (as on the homepage), and the one-gesture
/// "Clear filters" lives in the toolbar overflow (<c>LibraryV3Session.ClearAllFilters</c>).</para>
/// </summary>
sealed class LibraryV3Chips : Component
{
    static readonly int[] PrimaryCodes =
    [
        (int)SidebarV3Filter.Playlists, (int)SidebarV3Filter.Podcasts,
        (int)SidebarV3Filter.Albums, (int)SidebarV3Filter.Artists,
    ];

    static readonly int[] QualifierCodes =
    [
        (int)SidebarV3Qualifier.ByYou, (int)SidebarV3Qualifier.BySpotify, (int)SidebarV3Qualifier.Mixed,
    ];

    /// <summary>Node keys (and rove codes) for the qualifier options are offset so one handle map and one rove list can
    /// carry both levels of the rail.</summary>
    const int QualifierNodeKeyBase = 100;

    // ── the motion, copied verbatim from ConcertUi/HomeFacetChips (§3.2.16 defers to that grammar) ──────────────────────

    /// <summary>The loose pill's morph: position + a WIDTH reflow, 220 ms <c>SmoothOut</c>. Carried by every pill so the
    /// check mark appearing, the label changing width, and neighbours sliding are all one continuous motion.</summary>
    static readonly LayoutTransition PillMorph = new(
        TransitionChannels.Position | TransitionChannels.Size,
        TransitionDynamics.Tween(220f, Easing.SmoothOut),
        Size: SizeMode.Reflow, Axes: SizeAxes.Width);

    /// <summary>The fused pill's morph — the same recipe at ConcertUi's 260 ms, so growing INTO the compound shape reads
    /// slightly more deliberate than collapsing out of it.</summary>
    static readonly LayoutTransition FusedMorph = new(
        TransitionChannels.Position | TransitionChannels.Size,
        TransitionDynamics.Tween(260f, Easing.SmoothOut),
        Size: SizeMode.Reflow, Axes: SizeAxes.Width);

    /// <summary>A spilled option's legs: it enters with the group's reflow and, when picked, EXITS toward the pill
    /// (leftward) while fading — the leg that reads as "it flew into the pill and became its second segment".</summary>
    static readonly LayoutTransition OptionFly = new(
        TransitionChannels.Position | TransitionChannels.Opacity,
        TransitionDynamics.Tween(220f, Easing.FluentAccelerate),
        Exit: new EnterExit(Dx: -56f, Opacity: 0f, Active: true));

    /// <summary>The docked segment arrives FROM the option's side (right) at 300 ms, overlapping the option's exit leg —
    /// the two together are the dock.</summary>
    static readonly LayoutTransition SegmentDock = new(
        TransitionChannels.Position | TransitionChannels.Opacity,
        TransitionDynamics.Tween(300f, Easing.SmoothOut),
        Enter: new EnterExit(Dx: 56f, Opacity: 0.4f, Active: true));

    readonly LibraryV3Session _session;

    public LibraryV3Chips(LibraryV3Session session) => _session = session;

    public override Element Render()
    {
        var prefs = UseContext(SidebarPreferences.Slot);
        var hooks = UseContext(InputHooks.Current);
        var focus = UseSignal(0);
        var nodes = UseMemo(static () => new Dictionary<int, NodeHandle>(8), DepKey.Empty);
        // The rove order, rebuilt each render into a component-owned scratch list (never a signal — this is a buffer, not
        // state). It has to be derived BEFORE the elements, because whether an option is on the rail decides both the
        // focus clamp and which node the arrow keys move the focus visual to.
        var rove = UseMemo(static () => new List<int>(8), DepKey.Empty);

        int filter = prefs is null ? 0 : LibraryV3Metrics.NormalizeFilter(prefs.V3Filter.Value);
        int qualifier = prefs is null ? 0 : LibraryV3Metrics.NormalizeQualifier(prefs.V3Qualifier.Value);
        if (prefs is not null) _ = prefs.Entries.Version.Value;   // subscribe: QualifiersAvailable moves with the projection
        bool qualifiersAvailable = prefs?.Entries.QualifiersAvailable ?? false;

        // The qualifier is a property of Playlists and only exists when the data evidences it. `fused` is the compound
        // state; `spilled` is the intermediate one where the options are offered loose.
        bool qualifierRelevant = filter == (int)SidebarV3Filter.Playlists && qualifiersAvailable;
        bool fused = qualifierRelevant && qualifier != (int)SidebarV3Qualifier.Any;
        bool spilled = qualifierRelevant && !fused;

        // Auto-correction (§3.2.4 / §3.2.17). In an EFFECT, never in the render body: a preference write during render
        // would be a render-purity violation and would re-enter this component mid-flush.
        UseLayoutEffect(() =>
        {
            if (prefs is not { } pf) return;
            if (!qualifierRelevant && pf.V3Qualifier.Peek() != (int)SidebarV3Qualifier.Any)
                pf.SetV3Qualifier((int)SidebarV3Qualifier.Any);
        }, DepKey.From(qualifierRelevant ? 1 : 0, qualifier));

        if (prefs is not { } p) return new BoxEl();

        // ── the rove order ────────────────────────────────────────────────────────────────────────────────────────────
        rove.Clear();
        foreach (int code in PrimaryCodes)
        {
            rove.Add(code);
            if (code == filter && spilled)
                foreach (int q in QualifierCodes) rove.Add(QualifierNodeKeyBase + q);
        }
        int focusIdx = focus.Value;                       // subscribe: arrowing re-renders to move Focusable
        if (focusIdx < 0 || focusIdx >= rove.Count) focusIdx = 0;

        // ── the rail ──────────────────────────────────────────────────────────────────────────────────────────────────
        var children = new List<Element>(PrimaryCodes.Length * 2 + 2);
        // The rail's GROUP NAME (§3.2.12). The engine has no automation-name channel and no RadioButtons container role,
        // so the only place a group label can live is text — painted at zero opacity, never hit-testable.
        children.Add(new BoxEl
        {
            Key = "v3-filter-group",
            Opacity = 0f, HitTestVisible = false, Shrink = 0f,
            Children = [new TextEl(Loc.Get(Strings.Sidebar.A11y.FilterGroup)) { Size = 1f, MaxLines = 1 }],
        });

        int roveAt = 0;
        bool prevSpilledOptions = false;
        for (int i = 0; i < PrimaryCodes.Length; i++)
        {
            int code = PrimaryCodes[i];
            bool on = filter == code;
            bool isFused = on && fused;
            bool spillsHere = on && spilled;

            // No divider before a facet whose PREDECESSOR spilled its options: those options belong to that facet, and a
            // divider there makes them read as peers of the top-level kinds instead of children of one (the HomeFacetChips
            // rule). The divider is keyed on the facet it precedes so appearing/disappearing is an insert, not a shift.
            if (i > 0 && !prevSpilledOptions) children.Add(Divider(code));

            int pillIdx = roveAt++;
            var group = new List<Element>(QualifierCodes.Length + 1)
            {
                isFused
                    // FUSED: the option has flown in and become the pill's second segment. Tapping drops the qualifier and
                    // re-offers the options — one step back, the chevron's promise.
                    ? FusedPill(nodes, code, LibraryV3Labels.Filter(code), LibraryV3Labels.Qualifier(qualifier),
                                focusable: focusIdx == pillIdx,
                                onClick: () => p.SetV3Qualifier((int)SidebarV3Qualifier.Any))
                    // LOOSE: re-tapping the ACTIVE pill clears it — the pill IS the toggle (the ContentFilterChips
                    // convention), so there is no dead tap.
                    : Pill(nodes, code, "v3f" + code, LibraryV3Labels.Filter(code), on,
                           focusable: focusIdx == pillIdx, height: 28f, fontSize: 13f, animate: PillMorph,
                           onClick: () => SelectFilter(p, on ? (int)SidebarV3Filter.All : code)),
            };

            if (spillsHere)
            {
                foreach (int q in QualifierCodes)
                {
                    int qq = q;
                    int optIdx = roveAt++;
                    // Same 28-DIP height as the facets (one crisp row, HomeFacetChips' equal-size options) with the
                    // subordinate cue in the TYPE size + the tight group, not in a second chip height.
                    group.Add(Pill(nodes, QualifierNodeKeyBase + qq, "v3q" + qq, LibraryV3Labels.Qualifier(qq),
                                   selected: false, focusable: focusIdx == optIdx, height: 28f, fontSize: 12f,
                                   animate: OptionFly, onClick: () => p.SetV3Qualifier(qq)));
                }
            }
            prevSpilledOptions = spillsHere;

            // The group is ALWAYS present, even for a facet that owns no sub-filter. It has to be: the pill can only be
            // reused across the loose → fused swap if its PARENT is the same node in both renders, and a group that
            // appeared only while options were spilled would re-parent the pill on the very transition the fusion
            // depends on. A facet and its options share this one tight group (a half gap, no divider) so the rail reads
            // as "Playlists ▸ its options" instead of as seven peers.
            children.Add(new BoxEl
            {
                Key = "v3-facet-group:" + code,
                Direction = 0, AlignItems = FlexAlign.Center, Gap = 4f, Shrink = 0f,
                Children = [.. group],
            });
        }

        return Rail(children, e => Rove(e, hooks, nodes, focus, rove, c => Activate(p, c, filter, fused)));
    }

    // ── selection ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Keyboard activation of whatever the rove index names, kept identical to that chip's own <c>OnClick</c>:
    /// the fused facet drops its qualifier, an already-checked facet clears to All, anything else selects.</summary>
    void Activate(SidebarPreferences prefs, int roveCode, int filter, bool fused)
    {
        if (roveCode >= QualifierNodeKeyBase)
        {
            int q = roveCode - QualifierNodeKeyBase;
            prefs.SetV3Qualifier(prefs.V3Qualifier.Peek() == q ? (int)SidebarV3Qualifier.Any : q);
            return;
        }
        if (roveCode == filter && fused) { prefs.SetV3Qualifier((int)SidebarV3Qualifier.Any); return; }
        SelectFilter(prefs, roveCode == filter ? (int)SidebarV3Filter.All : roveCode);
    }

    /// <summary>Changing the kind filter clears the qualifier whenever the new kind is not Playlists, drops a Custom sort
    /// that only exists under Playlists (§3.2.6's fallback, persisted), and leaves any drill-in level — the folder you
    /// were inside may not even be part of the new kind set.</summary>
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

    // ── chrome ────────────────────────────────────────────────────────────────────────────────────────────────────────

    static Element Rail(List<Element> chips, Action<KeyEventArgs> onKey)
        => ScrollView(new BoxEl
        {
            Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center, MinWidth = 0f,
            // The ONE content lane (see LibraryV3Header's band). It sits INSIDE the horizontal scroller on purpose: the
            // chips must scroll out from under the lane with AutoEdgeFade, not be clipped at a padded viewport.
            Padding = SidebarPaneMetrics.BandInset,
            // The rail box owns the arrow-key handler; a focused chip's key event bubbles here.
            OnKeyDown = onKey,
            Children = [.. chips],
        }, horizontal: true) with
        {
            Grow = 0f, Height = LibraryV3Metrics.ChipRailHeight, AutoEdgeFade = true, SuppressScrollBar = true,
            ScrollKey = "sidebar.v3.chips",
        };

    /// <summary>A thin hairline between two top-level facets (HomeFacetChips' <c>Divider</c>, at the rail's scale).</summary>
    static Element Divider(int beforeCode) => new BoxEl
    {
        Key = "v3-facet-div:" + beforeCode,
        Width = 1f, Height = 18f, Shrink = 0f, Fill = Tok.StrokeDividerDefault,
    };

    /// <summary>The loose pill — a facet, or one of a facet's spilled options. Selected is an accent fill with a check
    /// mark; unselected is the pane's quiet subtle fill. The brush cross-fade IS the select/deselect colour motion
    /// (§3.2.16) and <paramref name="animate"/> carries the geometry, so there is no per-chip animation code.</summary>
    static Element Pill(Dictionary<int, NodeHandle> nodes, int nodeKey, string key, string label, bool selected,
                        bool focusable, float height, float fontSize, LayoutTransition animate, Action onClick)
    {
        var text = new TextEl(label)
        {
            Size = fontSize, Weight = (ushort)(selected ? 600 : 400),
            Color = selected ? Tok.TextOnAccentPrimary : Tok.TextPrimary,
            MaxLines = 1,
        };
        return new BoxEl
        {
            // KEYED, and the fused pill reuses this facet's key: that shared identity is the whole morph mechanism.
            Key = key,
            Animate = animate,
            Direction = 0, Height = height, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = 5f,
            // The check mark grows out of the leading padding rather than pushing the label, so the width change is the
            // pill's own and the text does not jump.
            Padding = new Edges4(selected ? 8f : 12f, 0f, 12f, 0f),
            Corners = Radii.FullAll,
            Fill = selected ? Tok.AccentDefault : Tok.FillSubtleSecondary,
            HoverFill = selected ? Tok.AccentSecondary : Tok.FillSubtleTertiary,
            PressedFill = selected ? Tok.AccentTertiary : Tok.FillSubtleTertiary,
            BrushTransitionMs = WaveeMotion.Fast,
            Role = AutomationRole.RadioButton, Cursor = CursorId.Hand,
            Focusable = focusable, FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
            OnClick = onClick,
            OnRealized = h => nodes[nodeKey] = h,
            Children = selected
                ?
                [
                    new BoxEl
                    {
                        Key = key + ":check", Width = 12f, Height = 12f, Shrink = 0f,
                        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        Animate = MotionRecipes.IconSwap,
                        Children = [Icon(Icons.Check, 10f, Tok.TextOnAccentPrimary)],
                    },
                    text,
                ]
                : [text],
        };
    }

    /// <summary>The FUSED facet pill: an accent capsule carrying a raised inner segment (the checked facet — the loose
    /// pill's visual survivor) followed by the chosen sub-filter's value and a chevron that re-offers the choice. Emitted
    /// under the facet's OWN pill key so the reconciler morphs the loose pill into this shape.</summary>
    static Element FusedPill(Dictionary<int, NodeHandle> nodes, int code, string facet, string value, bool focusable,
                            Action onClick) => new BoxEl
    {
        Key = "v3f" + code,
        Animate = FusedMorph,
        Direction = 0, Height = 28f, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = 6f,
        Padding = new Edges4(3f, 0f, 9f, 0f),
        Corners = Radii.FullAll,
        Fill = Tok.AccentDefault, HoverFill = Tok.AccentSecondary, PressedFill = Tok.AccentTertiary,
        BrushTransitionMs = WaveeMotion.Fast,
        Role = AutomationRole.RadioButton, Cursor = CursorId.Hand,
        Focusable = focusable, FocusVisualMargin = new Edges4(2f, 2f, 2f, 2f),
        OnClick = onClick,
        OnRealized = h => nodes[code] = h,
        Children =
        [
            new BoxEl   // the docked segment — a raised card capsule, the option's landing site
            {
                Key = "v3-facet-seg:" + code,
                Animate = SegmentDock,
                Direction = 0, Height = 22f, Shrink = 0f, AlignItems = FlexAlign.Center, Gap = 4f,
                Padding = new Edges4(8f, 0f, 8f, 0f), Corners = CornerRadius4.All(11f),
                Fill = Tok.FillCardDefault, Shadow = Elevation.Card,
                Children =
                [
                    Icon(Icons.Check, 10f, Tok.AccentTextPrimary) with { Shrink = 0f },
                    new TextEl(facet) { Size = 12f, Weight = 600, Color = Tok.AccentTextPrimary, MaxLines = 1 },
                ],
            },
            new TextEl(value) { Size = 12f, Weight = 600, Color = Tok.TextOnAccentPrimary, MaxLines = 1 },
            Icon(Icons.ChevronDown, 9f, Tok.TextOnAccentPrimary) with { Shrink = 0f },
        ],
    };

    /// <summary>Roving focus for the rail. Selection does NOT follow focus here (unlike <c>RadioButtons</c>): a filter is
    /// a data operation, so arrowing across the rail must not fire seven projections — Space/Enter commits. The code list
    /// is whatever is currently ON the rail, so the spilled options join the same single tab stop.</summary>
    static void Rove(KeyEventArgs e, InputHooks hooks, Dictionary<int, NodeHandle> nodes, Signal<int> focus,
                     List<int> codes, Action<int> select)
    {
        if (e.Handled) return;
        int n = codes.Count;
        if (n == 0) return;
        int cur = focus.Peek();
        if (cur < 0 || cur >= n) cur = 0;
        int next;
        switch (e.KeyCode)
        {
            case Keys.Left: next = cur == 0 ? n - 1 : cur - 1; break;
            case Keys.Right: next = cur == n - 1 ? 0 : cur + 1; break;
            case Keys.Home: next = 0; break;
            case Keys.End: next = n - 1; break;
            case Keys.Space:
            case Keys.Enter:
                select(codes[cur]);
                e.Handled = true;
                return;
            default:
                return;
        }
        focus.Value = next;
        if (nodes.TryGetValue(codes[next], out var h) && !h.IsNull)
            (hooks.MoveFocusVisual ?? hooks.RestoreFocus)?.Invoke(h);
        e.Handled = true;
    }
}
