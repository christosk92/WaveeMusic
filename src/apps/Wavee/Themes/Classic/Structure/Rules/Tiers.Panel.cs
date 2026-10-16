// ── Themes/Classic/Structure/Rules/Tiers.Panel.cs — Classic's side panels per window tier (themes plan 3.1, 3.6; TC2, D3) ────────────
//
// THE PANELS PER WINDOW TIER (boards C-Breakpoints "Side panels" row, C-Shell-Huge, C-Home-Huge4K, C-Album-Huge4K, C-Queue-Small,
// C-Queue-Compact, C-Lyrics-Compact, C-NowPlaying-Compact):
//
//   Compact   a BOTTOM SHEET: the whole window width from 56 down (8 under the title bar) to the window's bottom edge, over the page and
//             the mini bar, with its r8 top corners, a 16-tall grab strip (a 32 x 4 handle) and a 0.4 scrim from the title bar down; a
//             press on the scrim closes it. It slides up.
//   Small     FLOATING (C-Queue-Small): 340 wide, 8 from the right edge, 8 under the title bar and 8 over the player bar, r8, the ring and
//             the flyout shadow, over the page; the page keeps its width. Never docked. It slides in from the right.
//   Standard  ONE docked panel beside the page (the tuned boards): the user's width (200-500, 340 fresh), its resize grip.
//   Huge      TWO docked panels side by side (D3, C-Shell-Huge: Lyrics + Queue; C-Home-Huge4K: Now playing + Queue): the primary panel and
//             the queue in a second pane right of it, 8 between them, each 380 at 2560 and 420 at 3840 (a floor that grows linearly
//             between the two drawn widths; a user who widened the primary keeps it wider). At 3840 the Huge rule ("the same rules").
//
// The core keys docking on the COMMITTED window tier (`PanelPolicy.For(tier)`, these rules), so the old fit test (pane + panel + 480 of
// content) is gone: a crossing docks, floats, sheets or adds the second pane on the crossing frame, and the reservation's change FLIPs the
// content card and the panel host (plan 3.3). The board's "Float -> dock: page width animates 200 ms" is the content card's FLIP.
// The two-pane rung is the Huge window edge itself (1920 with its 40 narrowing hold): no separate rung is needed (plan check D3).
//
// Literal numbers are allowed in this file (WVTH003 exempts `Rules\**`).

using System.Collections.Immutable;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.Classic;

/// <summary>The side panels' record for one window tier.</summary>
/// <param name="Tier">The window tier.</param>
/// <param name="Rule">The placement the core lays the panel out by (docked, floating or a sheet; how many dock; the docked floor).</param>
/// <param name="FloatWidth">The floating panel's width (Small); unused elsewhere.</param>
/// <param name="Inset">The floating panel's inset from the right edge, the title bar and the player bar; the sheet's from the title bar.</param>
public sealed record ClassicPanelTier(WidthTier Tier, PanelTierRule Rule, float FloatWidth, float Inset);

/// <summary>Classic's side panels per window tier (public so <c>Wavee.Tests</c> pins them).</summary>
public static class ClassicPanelTiers
{
    /// <summary>The node keys of the primary panel's host and the second pane's (the tier tests find them).</summary>
    public const string HostKey = "shell.panel.host", SecondHostKey = "shell.panel.second";

    /// <summary>The floating panel's width (C-Queue-Small) and every inset of a floating panel and a sheet.</summary>
    public const float FloatW = 340f, Gap = 8f;

    /// <summary>The Huge panes' widths: 380 at 2560 and below, 420 at 3840 and above (C-Shell-Huge, C-Home-Huge4K).</summary>
    public const float HugeW = 380f, HugeAt = 2560f, Huge4KW = 420f, Huge4KAt = 3840f;

    /// <summary>Compact: the bottom sheet.</summary>
    public static ClassicPanelTier Compact { get; } = new(WidthTier.Compact, new PanelTierRule(PanelPlacement.Sheet), FloatW, Gap);

    /// <summary>Small: floating, 340.</summary>
    public static ClassicPanelTier Small { get; } = new(WidthTier.Small, new PanelTierRule(PanelPlacement.Float), FloatW, Gap);

    /// <summary>Standard: one docked panel.</summary>
    public static ClassicPanelTier Standard { get; } = new(WidthTier.Standard, PanelTierRule.Docked, FloatW, Gap);

    /// <summary>Huge: two docked panels (D3), each at least 380, growing to 420 at 3840.</summary>
    public static ClassicPanelTier Huge { get; } = new(WidthTier.Huge,
        new PanelTierRule(PanelPlacement.Dock, MaxDocked: 2, FloorW: HugeW, FloorAt: HugeAt, FloorMaxW: Huge4KW, FloorMaxAt: Huge4KAt), FloatW, Gap);

    private static readonly ClassicPanelTier[] s_tiers = [Compact, Small, Standard, Huge];

    /// <summary>The record of window tier <paramref name="window"/> (Huge 4K reads Huge's).</summary>
    public static ClassicPanelTier For(WidthTier window) => s_tiers[Math.Min((int)window, (int)WidthTier.Huge)];

    /// <summary>The record for <paramref name="facts"/>; Standard's where no frame resolved the tiers.</summary>
    public static ClassicPanelTier Of(in TierFacts facts) => For(facts.Policy is null ? WidthTier.Standard : facts.Window);

    /// <summary>The rules the structure publishes (<c>PanelPolicy.Tiers</c>, Compact first).</summary>
    public static ImmutableArray<PanelTierRule> Rules { get; } = ImmutableArray.Create(Compact.Rule, Small.Rule, Standard.Rule, Huge.Rule);

    // ── the sheet's chrome (C-Rail sheet="true", C-NowPlaying-Compact) ───────────────────────────────────────────────

    /// <summary>The grab strip's height and its handle.</summary>
    public const float GrabStripH = 16f, GrabBarW = 32f, GrabBarH = 4f;

    /// <summary>The scrim behind the sheet (black at 0.4).</summary>
    public static readonly ColorF Scrim = new(0f, 0f, 0f, 0.4f);

    /// <summary>The sheet's and the floating panel's corner.</summary>
    public const float Corner = 8f;

    /// <summary>The now-playing sheet's doors under the hero (Lyrics, Queue): 48 tall, 8 apart, inset 16 (C-NowPlaying-Compact).</summary>
    public const float SheetDoorH = 48f, SheetDoorGap = 8f, SheetDoorInset = 16f, SheetDoorGlyph = 16f, SheetDoorText = 12f, SheetDoorTextLine = 16f;
}
