// ── Themes/Zune/Structure/Rules/Tiers.Panel.cs — Zune's right panel per window tier (themes plan 3.2, 3.3, 6.3; P5 Z4a, ZT2) ──────────
//
// THE PANEL PER WINDOW TIER (boards Z-Part-Panel's `tier` enum, Z-Frame-Panel, Z-Frame-Panel-Small / -Compact, Z-Frame-Huge, Z-Frame-Huge4K;
// Z-Sizes-Breakpoints' "right panel" row):
//
//   tier       placement                 panes, left to right                       width
//   Compact    a full-width sheet         one, its pivots (queue · lyrics · …)       the window's
//   Small      floating 340, a shadow     one                                        340
//   Standard   inline beside the page     one                                        340
//   Huge       docked                     queue | lyrics · friends (LINKED)          720 (2 x 360)
//   Huge 4K    docked                     queue | lyrics | friends (LINKED)          1080 (3 x 360)
//
// THE ARRANGEMENT (the core's `PanelArrangement`, contract 1.29). Huge docks the queue LEFT of the primary panel (`PaneSide.Before`), Huge 4K
// adds friends RIGHT of it. The panes are LINKED: they open and close with the panel as one (the board draws the 720 and the 1080 as one
// surface), and the primary steps aside to lyrics when it would show the queue or friends (the board's docked2 with the queue picked:
// "queue | lyrics"). The primary's pivot head leaves the docked bodies out (Huge: lyrics · now playing · friends; Huge 4K: lyrics · now
// playing). The `maxDockedPanes` parameter caps the count (Panorama sets 2: queue | lyrics · friends at Huge 4K too; 1: the 340 panel at
// every docking tier), and `panelMode` AlwaysOverlay floats the panel at every desktop tier.
//
// THE WIDTHS. The panel is 340 and never resized (Min = Max, Z2a's hand-off). Where two or more dock, every column is the board's 360: the
// rule's FLOOR (the core docks the primary at its floor, above the policy's own bounds, and sizes each docked pane at it).
//
// HOW A CROSSING MOVES IT (Z-Sizes-Breakpoints "panels morph: sheet → overlay → inline → docked: the panel animates its width; the page column
// eases with it, no reflow snap"). Every move of a panel or a pane is carried by a SIZE channel (ZT1's lesson: a position slide is snapped by
// the engine's reflow-shove rule whenever any reflow runs on the same frame, and the title bar and the band reflow on these crossings): each
// host eases its own width (`HostMotion`), the spacer right of it eases its width (`ShiftMotion`) so the host rides the re-solve, and the
// floating panel's top spacer eases its height (`TopMotion`). The panes keep their nodes (hosts keyed by pane index, never by tier).
//
// Literal numbers are allowed in this file (WVTH003 exempts `Rules\**`). Public, so the tests pin them.

using System.Collections.Immutable;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Zune;

/// <summary>The panel's record for one window tier.</summary>
/// <param name="Tier">The window tier.</param>
/// <param name="Rule">The placement, the cap, the floor and the arrangement the core lays the panel out by.</param>
public sealed record ZunePanelTier(WidthTier Tier, PanelTierRule Rule)
{
    /// <summary>How many columns dock side by side (0 where the panel floats or is a sheet).</summary>
    public int Columns => Rule.Docks ? 1 + Rule.Arrangement.Count : 0;

    /// <summary>The docked panel's whole width (340, 720, 1080; 0 where it does not dock).</summary>
    public float DockedW => Columns switch
    {
        0 => 0f,
        1 => ZunePanelTiers.PanelW,
        _ => Columns * ZunePanelTiers.DockedPaneW,
    };
}

/// <summary>Zune's panel per window tier (public so the tests pin it).</summary>
public static class ZunePanelTiers
{
    /// <summary>The panel's one width inline and floating (Z-Part-Panel "inline" / "overlay"): never resized.</summary>
    public const float PanelW = 340f;

    /// <summary>A docked column where two or three dock (Z-Part-Panel "docked2" / "docked3"): 360 each, 720 and 1080 in all.</summary>
    public const float DockedPaneW = 360f;

    /// <summary>The third pane's host (the second's is <see cref="ZuneFrameRules.SecondPanelHostKey"/>).</summary>
    public const string ThirdPanelHostKey = "zune-panel-host-3";

    /// <summary>The host key of docked pane <paramref name="index"/> (0 the second pane, 1 the third): by index, never by tier, so a crossing
    /// that adds or drops a pane keeps the others' nodes.</summary>
    public static string PaneHostKey(int index) => index == 0 ? ZuneFrameRules.SecondPanelHostKey : ThirdPanelHostKey;

    /// <summary>Huge's arrangement: the queue left of the primary, linked; the primary steps aside to lyrics.</summary>
    public static PanelArrangement HugePanes { get; } = new(new DockedPane(PanelMode.Queue, PaneSide.Before), Linked: true, Fallback: PanelMode.Lyrics);

    /// <summary>Huge 4K's arrangement: the queue left of the primary and friends right of it, linked; the primary steps aside to lyrics.</summary>
    public static PanelArrangement Huge4KPanes { get; } = new(new DockedPane(PanelMode.Queue, PaneSide.Before),
        new DockedPane(PanelMode.Friends, PaneSide.After), Linked: true, Fallback: PanelMode.Lyrics);

    /// <summary>The panel's rule per window tier (Compact first) for <paramref name="mode"/> and a cap of <paramref name="maxDocked"/> panes
    /// (the `maxDockedPanes` parameter, clamped to 1-3).</summary>
    public static ImmutableArray<PanelTierRule> PanelRules(ZunePanelMode mode, int maxDocked)
    {
        var sheet = new PanelTierRule(PanelPlacement.Sheet);
        var floating = new PanelTierRule(PanelPlacement.Float);
        if (mode == ZunePanelMode.AlwaysOverlay) return ImmutableArray.Create(sheet, floating, floating, floating, floating);
        int cap = Math.Clamp(maxDocked, 1, 3);
        return ImmutableArray.Create(sheet, floating, new PanelTierRule(PanelPlacement.Dock), Docked(HugePanes, cap), Docked(Huge4KPanes, cap));
    }

    /// <summary>A docking rule over <paramref name="panes"/> capped at <paramref name="cap"/> columns: the 360 floor where two or more dock,
    /// the 340 panel alone (no floor, no arrangement) where one does.</summary>
    private static PanelTierRule Docked(PanelArrangement panes, int cap)
    {
        int columns = Math.Min(cap, 1 + panes.Count);
        if (columns <= 1) return new PanelTierRule(PanelPlacement.Dock);
        return new PanelTierRule(PanelPlacement.Dock, MaxDocked: columns, FloorW: DockedPaneW) { Panes = panes.Capped(columns - 1) };
    }

    /// <summary>The panel's record of window tier <paramref name="window"/> under the defaults (inline when there is room, three docked panes).</summary>
    public static ZunePanelTier Panel(WidthTier window) => For(window, ZunePanelMode.InlineWhenRoom, 3);

    /// <summary>The panel's record of window tier <paramref name="window"/> for <paramref name="mode"/> and <paramref name="maxDocked"/>.</summary>
    public static ZunePanelTier For(WidthTier window, ZunePanelMode mode, int maxDocked)
    {
        var rules = PanelRules(mode, maxDocked);
        return new ZunePanelTier(window, rules[Math.Clamp((int)window, 0, rules.Length - 1)]);
    }

    // ── how a crossing moves the panel (size channels only) ───────────────────────────────────────────────────────────

    /// <summary>A panel or pane host's width across a crossing (340 ↔ 360 at 1920, the sheet's window width ↔ 340 at 640): the dock's 300 ms on
    /// the smooth-out curve, laid out at the final width with the clip easing (a size channel; the host's place rides its spacers). Its
    /// descendants take their final layout (the panel's own slide is a paint translate, not a layout track).</summary>
    public static readonly LayoutTransition HostMotion = new(TransitionChannels.Size,
        TransitionDynamics.Tween(ZuneFrameRules.DockMs, Easing.SmoothOut), SizeMode.Reveal,
        ExitDynamics: TransitionDynamics.Tween(ZuneFrameRules.DockMs, Easing.SmoothOut), SuppressDescendantTransitions: true);

    /// <summary>The spacer right of a host (what the docked panes right of it reserve): its width eases through real layout over the dock's
    /// 300 ms, so the host left of it rides the re-solve (a pane added or dropped at 1920 or 3200, a panel opening beside a pane).</summary>
    public static readonly LayoutTransition ShiftMotion = new(TransitionChannels.Size,
        TransitionDynamics.Tween(ZuneFrameRules.DockMs, Easing.SmoothOut), Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Width);

    /// <summary>The spacer over the primary host (0 docked and as a sheet, the band's foot floating): its height eases through real layout over
    /// the dock's 300 ms, so the panel glides between under-the-band and full height on the 640 and 1008 crossings.</summary>
    public static readonly LayoutTransition TopMotion = new(TransitionChannels.Size,
        TransitionDynamics.Tween(ZuneFrameRules.DockMs, Easing.SmoothOut), Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Height);
}
