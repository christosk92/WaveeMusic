// ── Themes/WinUi/Structure/Rules/Tiers.Screens.cs — the screens' tier hooks and motion (themes plan 3.2, 6.2; P6 W8b) ────────────────────
//
// THE SCREENS FAMILY (Settings, the theme picker and options, setup, release notes, the report and crash dialogs). Boards: W-Settings (-Compact /
// -Small / -Huge), W-SettingsSections, W-ThemeOptions (-Compact / -Small / -Huge), W-Setup, W-ReleaseNotes, W-Feedback, W-Crash; the rules are the
// `W-Tiers` text ("Settings, setup, dialogs"):
//
//   · SettingsCards cap at 1000 in Standard and 1200 in Huge (Q2: no other page cap) and sit on the page's left edge, never centred;
//   · the settings SelectorBar scrolls on Compact (and wherever its seven tabs run past the page);
//   · a SettingsCard stacks its control UNDER its label below 560 CONTENT width: the named rung `StackRung` below (a content-axis mark,
//     committing at the line on widening and held 24 on narrowing like every WinUI edge);
//   · reading text (About, release notes, descriptions) caps at 760;
//   · ContentDialogs (feedback, crash, setup) go full screen below 641 and cap at 588 / 760 elsewhere.
//
// The per-tier records are in `Tiers.Screens.Settings.cs`, `.Setup.cs`, `.ReleaseNotes.cs` and `.Dialogs.cs`; this file only names the rung WinUI's
// policy carries and the motion a part of the screens uses on a crossing. Literal numbers are allowed in `Rules\**` (WVTH003). Public, so
// `Wavee.Tests` pins them (the assembly has no InternalsVisibleTo).

using System.Collections.Immutable;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Themes.WinUi;

public static partial class WinUiTiers
{
    static partial void RungsScreens(ImmutableArray<NamedRung>.Builder rungs)
        => rungs.Add(new NamedRung(WinUiScreenRules.StackRung, new TierEdge(WinUiScreenRules.StackBelow, NarrowHold: WinUiScreenRules.StackHold, WidenHold: 0f), RungAxis.Content));

    static partial void LaddersScreens(ImmutableArray<NamedLadder>.Builder ladders) { }
}

/// <summary>The screens' tier-independent marks.</summary>
public static class WinUiScreenRules
{
    /// <summary>The rung a SettingsCard stacks under (the content width reaches it from below: 560 and wider keep label | control).</summary>
    public const string StackRung = "winui.screens.stack";

    /// <summary>The content width the cards stack below (W-Tiers: 560).</summary>
    public const float StackBelow = 560f;

    /// <summary>The hold on narrowing (WinUI's 24).</summary>
    public const float StackHold = 24f;

    /// <summary>The 48 title row a Compact sheet (the setup wizard) sits under.</summary>
    public const float SheetChromeRow = 48f;

    /// <summary>The Settings options in the board's order (W-ThemeOptions), by qualified id: Backdrop, Navigation pane, Density, Tabs in the title bar, Artwork in track rows,
    /// Use the Windows accent colour. The page draws exactly these six and whatever else the theme might add after them.</summary>
    public static readonly string[] OptionOrder =
    [
        "style.backdrop", "structure.navigationPane", "style.rowDensity", "structure.tabs", "structure.artworkInRows", "style.useWindowsAccent",
    ];

    /// <summary>Reading text caps here (About, release notes, descriptions).</summary>
    public const float ReadingCap = 760f;

    /// <summary>True when the cards of <paramref name="facts"/> keep their control beside the label (the content is at least <see cref="StackBelow"/>
    /// wide). A view mounted alone (no policy) keeps them beside.</summary>
    public static bool CardsStack(in TierFacts facts)
        => facts.Policy is not null && facts.Policy.IndexOfRung(StackRung) >= 0 && !facts.Rung(StackRung);
}

/// <summary>The motion of a part of the screens across a tier crossing.</summary>
public static class WinUiScreenMotion
{
    /// <summary>The reflow's duration.</summary>
    public const float ReflowMs = 280f;

    /// <summary>A surviving part that moves AND resizes: position and size tween, the size revealed.</summary>
    public static readonly LayoutTransition Reflow = new(
        TransitionChannels.Position | TransitionChannels.Size, TransitionDynamics.Tween(ReflowMs, Easing.SmoothOut), SizeMode.Reveal);

    /// <summary>A surviving cell that only moves.</summary>
    public static readonly LayoutTransition Shove = new(TransitionChannels.Position, TransitionDynamics.Tween(ReflowMs, Easing.SmoothOut));

    /// <summary>A part the new tier draws as a different tree enters and leaves on its opacity alone.</summary>
    public static readonly EnterExit SwapFade = SwapFamilies.Declare("WinUi.Screens.SwapFade", new(Opacity: 0f, Active: true));

    /// <summary>The cross-fade's timing.</summary>
    public static MotionTokenDef SwapFadeMotion => MotionTok.ControlFast;
}
