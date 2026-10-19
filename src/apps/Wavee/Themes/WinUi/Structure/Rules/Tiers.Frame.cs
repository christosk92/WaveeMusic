// ── Themes/WinUi/Structure/Rules/Tiers.Frame.cs — the title bar's record per window tier (themes plan 6.2; P6 W2a) ──────────────────────
//
// Board W-Tiers ("Title bar: tabs stay on in every tier") and Shell-Top. The title bar reads `WinUiTiers.TitleBar(tier)`, never a width:
//
//   tier       tab form (40 row)              toolbar (48 row)                                                  tabs off (one 48 bar)
//   Compact    Compact(140 selected, 36 icon)  hamburger, Back · Search button · bell, avatar                     the same, the captions at its end; no mark
//   Small      Ladder(160 -> 110, then scroll) hamburger, Back, Forward · search filling the middle (max 520) ·    the Wavee mark after Forward
//                                              bell, ⋯ (pin, Friends), avatar
//   Standard   Ladder(200)                     hamburger, Back, Forward · search 520 on the window's centre line ·  the Wavee mark after Forward
//                                              pin, Friends, bell, avatar
//   Huge       Ladder(240)                     the same with a 640 search and the profile name after the avatar     the Wavee mark after Forward
//
// The tab form is the engine strip's (`TabStripForm`, Chrome appearance), resolved against the bar's measured tab lane.

using FluentGpu.Controls;
using Wavee.Theming.Contracts;
using Wavee.Theming;

namespace Wavee.Themes.WinUi;

public static partial class WinUiTiers
{
    private static readonly WinUiTitleBarLayout[] s_titleBars =
    [
        new(WidthTier.Compact, TabStripForm.Compact(140f, 36f), TitleBarCenterAlign.BetweenClusters,
            SearchField: false, SearchW: 0f, Forward: false, PinAndFriends: false, More: false, Name: false, Mark: false,
            DragStrip: WinUiFrameRules.CompactDragStrip),
        new(WidthTier.Small, TabStripForm.Ladder(160f, 110f), TitleBarCenterAlign.BetweenClusters,
            SearchField: true, SearchW: WinUiFrameRules.SearchW, Forward: true, PinAndFriends: false, More: true, Name: false, Mark: true,
            DragStrip: WinUiFrameRules.DragStrip),
        new(WidthTier.Standard, TabStripForm.Ladder(200f), TitleBarCenterAlign.Window,
            SearchField: true, SearchW: WinUiFrameRules.SearchW, Forward: true, PinAndFriends: true, More: false, Name: false, Mark: true,
            DragStrip: WinUiFrameRules.DragStrip),
        new(WidthTier.Huge, TabStripForm.Ladder(240f), TitleBarCenterAlign.Window,
            SearchField: true, SearchW: WinUiFrameRules.SearchHugeW, Forward: true, PinAndFriends: true, More: false, Name: true, Mark: true,
            DragStrip: WinUiFrameRules.DragStrip),
    ];

    /// <summary>The title bar's record for window tier <paramref name="window"/> (Huge's from 1920 up).</summary>
    public static WinUiTitleBarLayout TitleBar(WidthTier window) => s_titleBars[Math.Clamp((int)window, 0, s_titleBars.Length - 1)];

    /// <summary>The title bar's record for <paramref name="facts"/>; Standard's where no frame resolved the tiers.</summary>
    public static WinUiTitleBarLayout TitleBarOf(in TierFacts facts) => TitleBar(facts.Policy is null ? WidthTier.Standard : facts.Window);
}

/// <summary>What WinUI's title bar draws in one window tier (W-Tiers, Shell-Top).</summary>
/// <param name="Tier">The window tier.</param>
/// <param name="TabForm">The strip's form: a width ladder, or the selected tab labelled and the others icon tabs.</param>
/// <param name="CenterAlign">Where the search sits: on the window's centre line (Standard, Huge) or between the clusters (Compact, Small).</param>
/// <param name="SearchField">The search is the field (false: the magnifier that opens it in a flyout).</param>
/// <param name="SearchW">The field's width: 520, 640 at Huge; at Small its widest, the field filling the middle.</param>
/// <param name="Forward">Forward is beside Back.</param>
/// <param name="PinAndFriends">Pin and Friends are in the toolbar (Small folds them into ⋯; Compact has neither).</param>
/// <param name="More">The ⋯ that lists pin and Friends.</param>
/// <param name="Name">The profile name beside the avatar.</param>
/// <param name="Mark">Tabs off shows the Wavee mark after Forward.</param>
/// <param name="DragStrip">The drag strip before the caption buttons.</param>
public sealed record WinUiTitleBarLayout(WidthTier Tier, TabStripForm TabForm, TitleBarCenterAlign CenterAlign, bool SearchField, float SearchW,
    bool Forward, bool PinAndFriends, bool More, bool Name, bool Mark, float DragStrip)
{
    /// <summary>The field fills the middle up to <see cref="SearchW"/> (Small) instead of standing at it.</summary>
    public bool SearchFills => CenterAlign == TitleBarCenterAlign.BetweenClusters && SearchField;
}
