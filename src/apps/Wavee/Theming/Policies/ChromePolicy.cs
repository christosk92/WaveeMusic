// ── Theming/Policies/ChromePolicy.cs — what a structure decides about the title-bar row (themes plan 5.5) ─────────────
//
// The merged chrome row (tabs · window-centred search · identity, `Shell.Chrome`) is allocated by SPACE ACCOUNTING: the fixed
// budget of everything that is neither a tab nor the search is subtracted from the window width and what is left is handed
// out in one priority order. The accounting is core (`Shell.Chrome.Resolve`); the BUDGET (what each affordance costs, where
// each optional island is allowed to appear, how wide the search and the tabs may be) is the structure's, and arrives as this
// value. `Default` is the shell before themes (what Wavee Classic declares), taken from the row's own named widths.

using Wavee.Theming.Contracts;

namespace Wavee.Theming;

/// <summary>The title-bar row's budget: widths in DIP unless a name says otherwise.</summary>
/// <param name="NameEnterW">The window width from which the profile NAME beside the avatar is allowed (lowest priority).</param>
/// <param name="ActionsEnterW">The window width from which bell, friends and pin enter the row together (below: folded into the menu).</param>
/// <param name="ForwardEnterW">The window width above which the forward button is allowed.</param>
/// <param name="PromotionHysteresisW">A promotion is re-resolved this much narrower, so a decision is sticky; a demotion is immediate.</param>
/// <param name="BarLeadW">The lead column before the tabs (root padding, pane toggle, left pad).</param>
/// <param name="NavButtonW">One bar-nav affordance's advance: back, forward and each trailing action.</param>
/// <param name="AddSlotW">The tab strip's permanently reserved "+" slot.</param>
/// <param name="ProfileChipW">The profile chip with the avatar alone.</param>
/// <param name="ConnectingChipW">The "Connecting" caption chip.</param>
/// <param name="SignInChipW">The "Sign in" accent button.</param>
/// <param name="ReconnectChipW">The "Reconnect" accent button.</param>
/// <param name="ProfileNameW">What showing the display name adds to the avatar chip.</param>
/// <param name="MinDragStripW">The grabbable seam kept before the caption-adjacent search icon.</param>
/// <param name="CaptionClusterW">The native caption buttons.</param>
/// <param name="GutterMinW">Reserved on each flank of the centre island.</param>
/// <param name="WidthQuantumW">Search and tab widths snap down to this, so a pixel of resize re-renders nothing.</param>
/// <param name="SearchMaxW">The widest the search field grows.</param>
/// <param name="SearchMinW">The narrowest the search field may be laid out (below it the row shows the icon).</param>
/// <param name="SearchWidthRatio">The search field's share of the window width.</param>
/// <param name="SearchIconW">The click-to-expand magnifier form of the search.</param>
/// <param name="TabMaxW">The widest a tab grows.</param>
/// <param name="TabMinW">The narrowest a tab is allocated.</param>
/// <param name="PinnedTabW">A pinned tab.</param>
/// <param name="TabViewportMinW">The smallest the tabs viewport may shrink to.</param>
public sealed record ChromeBudget(
    float NameEnterW, float ActionsEnterW, float ForwardEnterW, float PromotionHysteresisW,
    float BarLeadW, float NavButtonW, float AddSlotW,
    float ProfileChipW, float ConnectingChipW, float SignInChipW, float ReconnectChipW, float ProfileNameW,
    float MinDragStripW, float CaptionClusterW, float GutterMinW,
    float WidthQuantumW, float SearchMaxW, float SearchMinW, float SearchWidthRatio, float SearchIconW,
    float TabMaxW, float TabMinW, float PinnedTabW, float TabViewportMinW)
{
    /// <summary>The shell before themes: the row's named widths (<c>Shell.Layout.Chrome*</c>).</summary>
    public static ChromeBudget Default { get; } = new(
        Shell.Layout.ChromeNameEnterW, Shell.Layout.ChromeActionsEnterW, Shell.Layout.ChromeForwardEnterW,
        Shell.Layout.ChromePromotionHysteresisW,
        Shell.Layout.ChromeBarLeadW, Shell.Layout.ChromeNavButtonW, Shell.Layout.ChromeAddSlotW,
        Shell.Layout.ChromeProfileChipW, Shell.Layout.ChromeConnectingChipW, Shell.Layout.ChromeSignInChipW,
        Shell.Layout.ChromeReconnectChipW, Shell.Layout.ChromeProfileNameW,
        Shell.Layout.ChromeMinDragStripW, Shell.Layout.ChromeCaptionClusterW, Shell.Layout.ChromeGutterMinW,
        Shell.Layout.ChromeWidthQuantumW, Shell.Layout.ChromeSearchMaxW, Shell.Layout.ChromeSearchMinW,
        Shell.Layout.ChromeSearchWidthRatio, Shell.Layout.ChromeSearchIconW,
        Shell.Layout.ChromeTabMaxW, Shell.Layout.ChromeTabMinW, Shell.Layout.ChromePinnedTabW, Shell.Layout.ChromeTabViewportMinW)
    {
        SearchFocusGrowW = Shell.Layout.ChromeSearchFocusGrowW, SearchFocusClearW = Shell.Layout.ChromeSearchFocusClearW,
    };

    /// <summary>What the search field GROWS by while it has focus (or its suggestions are open): the focused field is
    /// <c>min(rest + this, room − <see cref="SearchFocusClearW"/>)</c>, where the room is the span between the tabs island and the
    /// trailing island. 0 (the default) keeps the field at its rest width.</summary>
    public float SearchFocusGrowW { get; init; }

    /// <summary>The clearance the FOCUSED field keeps inside the room between the tabs island and the trailing island (split
    /// evenly by the two drag bands either side), so a widened field never covers a tab or the avatar.</summary>
    public float SearchFocusClearW { get; init; }

    /// <summary>The tabs island is ONE tab-count button instead of the tab strip (a Compact title bar: the count opens the open tabs as
    /// a menu). The allocator then budgets the island at <see cref="NavButtonW"/> and reserves no "+" slot. False (the default) keeps
    /// the strip.</summary>
    public bool TabsAsCount { get; init; }

    /// <summary>The trailing island (the identity chip and its actions) is not in the row: the window tier carries the account in the nav
    /// drawer instead (Classic's Compact title bar, boards C-Shell-Compact and C-Sidebar's account row; TC2). False (the default) keeps it.</summary>
    public bool TrailingInDrawer { get; init; }

    /// <summary>Everything wrong with the budget, empty when the allocator can run on it.</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        void NonNegative(string name, float v)
        {
            if (!float.IsFinite(v) || v < 0f) problems.Add($"Chrome.Budget.{name} must be zero or more and finite (was {v})");
        }
        NonNegative(nameof(NameEnterW), NameEnterW);
        NonNegative(nameof(ActionsEnterW), ActionsEnterW);
        NonNegative(nameof(ForwardEnterW), ForwardEnterW);
        NonNegative(nameof(PromotionHysteresisW), PromotionHysteresisW);
        NonNegative(nameof(BarLeadW), BarLeadW);
        NonNegative(nameof(NavButtonW), NavButtonW);
        NonNegative(nameof(AddSlotW), AddSlotW);
        NonNegative(nameof(ProfileChipW), ProfileChipW);
        NonNegative(nameof(ConnectingChipW), ConnectingChipW);
        NonNegative(nameof(SignInChipW), SignInChipW);
        NonNegative(nameof(ReconnectChipW), ReconnectChipW);
        NonNegative(nameof(ProfileNameW), ProfileNameW);
        NonNegative(nameof(MinDragStripW), MinDragStripW);
        NonNegative(nameof(CaptionClusterW), CaptionClusterW);
        NonNegative(nameof(GutterMinW), GutterMinW);
        NonNegative(nameof(SearchFocusGrowW), SearchFocusGrowW);
        NonNegative(nameof(SearchFocusClearW), SearchFocusClearW);
        NonNegative(nameof(SearchIconW), SearchIconW);
        NonNegative(nameof(TabViewportMinW), TabViewportMinW);
        NonNegative(nameof(PinnedTabW), PinnedTabW);
        if (!float.IsFinite(WidthQuantumW) || WidthQuantumW <= 0f) problems.Add($"Chrome.Budget.WidthQuantumW must be positive and finite (was {WidthQuantumW})");
        if (!float.IsFinite(SearchMinW) || SearchMinW <= 0f) problems.Add($"Chrome.Budget.SearchMinW must be positive and finite (was {SearchMinW})");
        if (!float.IsFinite(SearchMaxW) || SearchMaxW < SearchMinW) problems.Add("Chrome.Budget.SearchMaxW must not be below SearchMinW");
        if (!float.IsFinite(SearchWidthRatio) || SearchWidthRatio < 0f) problems.Add($"Chrome.Budget.SearchWidthRatio must be zero or more and finite (was {SearchWidthRatio})");
        if (!float.IsFinite(TabMinW) || TabMinW <= 0f) problems.Add($"Chrome.Budget.TabMinW must be positive and finite (was {TabMinW})");
        if (!float.IsFinite(TabMaxW) || TabMaxW < TabMinW) problems.Add("Chrome.Budget.TabMaxW must not be below TabMinW");
        if (problems.Count == 0 && ActionsEnterW > NameEnterW) problems.Add("Chrome.Budget.ActionsEnterW must not exceed NameEnterW (the name never appears before the actions)");
        return problems;
    }
}

/// <summary>The title-bar row's policy.</summary>
/// <param name="Budget">The row's budget (every window tier that has no budget of its own).</param>
public sealed record ChromePolicy(ChromeBudget Budget)
{
    /// <summary>The shell before themes: the row's named widths, with the Compact row's tab count (and its account in the drawer, TC2), its narrowed spacers (no lead pad,
    /// the 16 drag strip, no centre-island floors) and the Huge row's wider search and tabs (what Wavee Classic declares; under <see cref="TierPolicy.Standard"/> only the
    /// base budget is ever read).</summary>
    public static ChromePolicy Default { get; } = new(ChromeBudget.Default)
    {
        Compact = ChromeBudget.Default with
        {
            TabsAsCount = true, TrailingInDrawer = true,
            BarLeadW = Shell.Layout.ChromeBarLeadW - FluentGpu.Controls.TitleBarSpacing.Default.LeadPad,
            MinDragStripW = Shell.Layout.ChromeCompactDragStripW, GutterMinW = 0f,
        },
        Huge = ChromeBudget.Default with { SearchMaxW = Shell.Layout.ChromeHugeSearchMaxW, TabMaxW = Shell.Layout.ChromeHugeTabMaxW },
    };

    /// <summary>The row's budget while the window is Compact; null keeps <see cref="Budget"/>.</summary>
    public ChromeBudget? Compact { get; init; }

    /// <summary>The row's budget while the window is Huge or Huge 4K; null keeps <see cref="Budget"/>.</summary>
    public ChromeBudget? Huge { get; init; }

    /// <summary>The budget the row is allocated by in window tier <paramref name="window"/>.</summary>
    public ChromeBudget BudgetFor(WidthTier window) => window switch
    {
        WidthTier.Compact => Compact ?? Budget,
        WidthTier.Huge or WidthTier.Huge4K => Huge ?? Budget,
        _ => Budget,
    };

    /// <summary>The budget for the window tier of <paramref name="tiers"/>; <see cref="Budget"/> where no frame resolved the tiers.</summary>
    public ChromeBudget BudgetFor(in TierFacts tiers) => tiers.Policy is null ? Budget : BudgetFor(tiers.Window);

    /// <summary>Everything wrong with the policy: the base budget and each tier's own.</summary>
    public IReadOnlyList<string> Validate()
    {
        if (Budget is null) return new[] { "Chrome.Budget is missing" };
        var problems = new List<string>(Budget.Validate());
        if (Compact is { } compact) foreach (string p in compact.Validate()) problems.Add("Compact: " + p);
        if (Huge is { } huge) foreach (string p in huge.Validate()) problems.Add("Huge: " + p);
        return problems;
    }
}
