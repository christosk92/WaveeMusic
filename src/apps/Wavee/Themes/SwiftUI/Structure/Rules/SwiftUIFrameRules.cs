// ── Themes/SwiftUI/Structure/Rules/SwiftUIFrameRules.cs — the SwiftUI spike frame's numbers (SPIKE, not for release) ──────────────────
//
// The frame is macOS 26's NavigationSplitView: a FLOATING GLASS SIDEBAR inset from the window on every side (8), its corners concentric with
// the window's (Tahoe's toolbar windows are ~26 round, so 26 - 8 = 18), the detail column to its right with the toolbar on top, and the
// player as a FLOATING GLASS CAPSULE over the bottom of the detail column (Apple Music's mini player: 12 from the column's sides and foot,
// the content scrolling under it). The borrowed WinUI structure still sizes the pieces: its title bar (48 toolbar; the 40 tab strip is hidden
// by the composition), its play bar (72; Mini 64 at Compact) and its page insets (16 / 24 / 36 / 48 by window tier, what the lint guide checks).

using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.SwiftUI;

/// <summary>The SwiftUI spike frame's numbers.</summary>
public static class SwiftUIFrameRules
{
    // ── the glass sidebar ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The sidebar's inset from the window and from the detail column (Tahoe's floating sidebar).</summary>
    public const float SidebarInset = 8f;

    /// <summary>The sidebar's corner: concentric with a ~26 window corner around an 8 inset.</summary>
    public const float SidebarRadius = 18f;

    /// <summary>The sidebar's top padding inside the glass (where macOS draws the traffic lights; the pane's own head starts under it).</summary>
    public const float SidebarTopPad = 8f;

    /// <summary>The glass rim's width.</summary>
    public const float RimWidth = 1f;

    /// <summary>The nav column's width for the core's pane width <paramref name="pane"/> (0 = no pane: no glass either).</summary>
    public static float NavColumnW(float pane) => pane > 0f ? pane + SidebarInset * 2f : 0f;

    public static readonly Edges4 SidebarMargin = Edges4.All(SidebarInset);

    // ── the glass capsule player ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The capsule's distance from the detail column's sides and foot.</summary>
    public const float CapsuleSide = 12f, CapsuleFoot = 12f;

    /// <summary>The capsule's inner side padding, so the bar's 16 inset art clears the round ends.</summary>
    public const float CapsulePad = 10f;

    public static readonly Edges4 CapsuleMargin = new(CapsuleSide, 0f, CapsuleSide, CapsuleFoot);
    public static readonly Edges4 CapsulePadding = new(CapsulePad, 0f, CapsulePad, 0f);

    /// <summary>The play bar's height (WinUI's: Mini 64 at Compact, 72 elsewhere).</summary>
    public static float BarH(WidthTier window) => window == WidthTier.Compact ? 64f : 72f;

    /// <summary>What a docked or floating panel leaves free under it: the capsule and its foot.</summary>
    public static float PanelFoot(WidthTier window) => BarH(window) + CapsuleFoot;

    // ── the title bar ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The title bar slot: WinUI's 48 toolbar (88 if a tab strip is ever shown).</summary>
    public static float TitleBarH(bool tabs) => tabs ? 88f : 48f;

    /// <summary>The toolbar search field's widest (macOS 26's toolbar search is a compact capsule, not WinUI's 520 omnibox).</summary>
    public const float SearchMaxW = 320f;

    /// <summary>The search field's width: the base's allocation, never wider than <see cref="SearchMaxW"/>.</summary>
    public static Func<float> ToolbarSearchWidth(Func<float> allocated) => () => MathF.Min(allocated(), SearchMaxW);

    /// <summary>The page inset by window tier (WinUI's, the borrowed pages draw on it): 16 / 24 / 36 / 48.</summary>
    public static float InsetX(WidthTier window) => window switch
    {
        WidthTier.Compact => 16f,
        WidthTier.Small => 24f,
        WidthTier.Standard => 36f,
        _ => 48f,
    };

    // ── the panel layer ──────────────────────────────────────────────────────────────────────────────────────────────────

    public const float PanelFloatW = 340f;
    public static readonly ColorF SheetScrim = ColorF.FromRgba(0, 0, 0, 0x4D);
    public const float SheetScrimFadeMs = 167f;

    /// <summary>A docked panel's corners: the glass card's 12 on the leading side.</summary>
    public static CornerRadius4 PaneBandCorners => new(Radii.Card, 0f, 0f, Radii.Card);

    // ── motion (WinUI's frame durations, so the borrowed views' own motion lines up) ─────────────────────────────────────

    public const float FrameMs = 167f, SlideMs = 250f;

    public static readonly LayoutTransition NavColumnAnim = new(TransitionChannels.Size,
        TransitionDynamics.Tween(FrameMs, Easing.FluentPane), Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Width);

    public static readonly LayoutTransition ContentCardAnim = new(TransitionChannels.Position | TransitionChannels.Size,
        TransitionDynamics.Tween(FrameMs, Easing.FluentPane), SizeMode.Reveal,
        ExitDynamics: TransitionDynamics.Tween(FrameMs, Easing.FluentPane), SuppressDescendantTransitions: true);

    public static readonly LayoutTransition PanelReserveAnim = new(TransitionChannels.Size,
        TransitionDynamics.Tween(SlideMs, Easing.FluentDecelerate), Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Width);

    public static readonly LayoutTransition PanelHostAnim = ContentCardAnim;

    public static readonly LayoutTransition TitleSlotAnim = new(TransitionChannels.Size,
        TransitionDynamics.Tween(FrameMs, Easing.SmoothOut), Size: SizeMode.Reflow, Anchor: SizeAnchor.Trailing, Axes: SizeAxes.Height);

    // ── node keys (the frame harness's and the lint guide's: the same as every theme's) ──────────────────────────────────

    public const string TitleBarKey = "shell.titlebar", ContentKey = "shell.content.card", PlayerBarKey = "shell.playerbar",
        NavColumnKey = "shell.nav.column", SidebarGlassKey = "swiftui.sidebar.glass", CapsuleKey = "swiftui.player.capsule",
        PanelHostKey = "swiftui.panel.host", SecondHostKey = "swiftui.panel.host.second", PageTitleKey = "winui.page.title";

    public const string ContentRowMorphId = "swiftui.content-row";

    public static string PaneHostKey(int index) => "swiftui.panel.pane:" + index;
}
