using System;

namespace Wavee;

/// <summary>Which shape the merged row's search takes at this width.</summary>
public enum MergedSearchMode : byte { Field, Icon }

/// <summary>
/// Pure pressure allocator for Wavee's single 48-DIP chrome row. Tabs are never projected out: their measured natural
/// extent only decides whether the centred search may occupy a full field or must yield to the caption-adjacent icon.
/// Structural promotions carry the standard 40-DIP reserve; demotions are immediate.
/// </summary>
public readonly record struct MergedChromeLayout(
    bool ShowName,
    bool ShowFriends,
    bool ShowForward,
    bool ShowBack,
    bool ShowNewTab,
    bool ShowTrailing,
    MergedSearchMode SearchMode,
    float SearchWidth)
{
    public bool FriendsInRow => ShowFriends;
    public bool FriendsInMenu => !ShowFriends;
    public bool BareAvatar => !ShowName && !ShowFriends;

    internal int Richness =>
        (ShowName ? 1 : 0) + (ShowFriends ? 1 : 0) + (ShowForward ? 1 : 0)
        + (ShowBack ? 1 : 0) + (ShowNewTab ? 1 : 0) + (ShowTrailing ? 1 : 0)
        + (SearchMode == MergedSearchMode.Field ? 1 : 0) + (int)(SearchWidth * 0.1f);

    public static MergedChromeLayout FromWidth(float width, int tabCount)
        => Resolve(width, EstimatedTabExtent(tabCount), null);

    public static MergedChromeLayout Resolve(float width, int tabCount, MergedChromeLayout? previous = null)
        => Resolve(width, EstimatedTabExtent(tabCount), previous);

    public static MergedChromeLayout Resolve(float width, float naturalTabExtent, MergedChromeLayout? previous = null)
    {
        width = MathF.Max(0f, width);
        naturalTabExtent = MathF.Max(ShellResponsiveLayout.ChromeTabViewportMinW, naturalTabExtent);
        var candidate = StageFor(width, naturalTabExtent);
        if (previous is not { } old) return Compose(width, in candidate);

        var reserved = StageFor(
            MathF.Max(0f, width - ShellResponsiveLayout.ChromePromotionHysteresisW), naturalTabExtent);
        var held = new Stage(
            candidate.Name && (old.ShowName || reserved.Name),
            candidate.Friends && (old.ShowFriends || reserved.Friends),
            candidate.Forward && (old.ShowForward || reserved.Forward),
            candidate.Back && (old.ShowBack || reserved.Back),
            candidate.NewTab && (old.ShowNewTab || reserved.NewTab),
            candidate.Trailing && (old.ShowTrailing || reserved.Trailing),
            candidate.Field && (old.SearchMode == MergedSearchMode.Field || reserved.Field));
        return Compose(width, in held);
    }

    public float FixedBudgetFor()
        => FixedBudget(ShowName, ShowFriends, ShowForward, ShowBack, ShowNewTab, ShowTrailing);

    public float FootprintFor(float naturalTabExtent)
        => FixedBudgetFor()
         + (SearchMode == MergedSearchMode.Field ? SearchWidth : ShellResponsiveLayout.ChromeSearchIconW)
         + MathF.Min(MathF.Max(naturalTabExtent, ShellResponsiveLayout.ChromeTabViewportMinW),
                     ShellResponsiveLayout.ChromeTabComfortMaxW);

    public static float EstimatedTabExtent(int tabCount, int pinnedCount = 0)
    {
        int open = Math.Max(1, tabCount);
        int pinned = Math.Clamp(pinnedCount, 0, open);
        return pinned * ShellResponsiveLayout.ChromePinnedTabW
             + (open - pinned) * ShellResponsiveLayout.ChromeTabMinW;
    }

    public static float ComfortableTabExtent(float naturalTabExtent)
        => Math.Clamp(
            QuantiseUp(naturalTabExtent * ShellResponsiveLayout.ChromeTabComfortRatio),
            ShellResponsiveLayout.ChromeTabComfortMinW,
            ShellResponsiveLayout.ChromeTabComfortMaxW);

    public static float PreferredSearchWidth(float width)
        => QuantiseDown(Math.Clamp(
            width * ShellResponsiveLayout.ChromeSearchWidthRatio,
            ShellResponsiveLayout.ChromeSearchMinW,
            ShellResponsiveLayout.ChromeSearchMaxW));

    public static float FixedBudget(bool name, bool friends, bool forward, bool back, bool newTab, bool trailing)
        => ShellResponsiveLayout.ChromeBarLeadW
         + ShellResponsiveLayout.ChromeThemeToggleW
         + (back ? ShellResponsiveLayout.ChromeNavButtonW : 0f)
         + (forward ? ShellResponsiveLayout.ChromeNavButtonW : 0f)
         + (newTab ? ShellResponsiveLayout.ChromeAddSlotW : 0f)
         + (trailing ? ShellResponsiveLayout.ChromeProfileChipW : 0f)
         + (trailing && name ? ShellResponsiveLayout.ChromeProfileNameW : 0f)
         + (friends ? ShellResponsiveLayout.ChromeNavButtonW : 0f)
         + (trailing && !forward ? ShellResponsiveLayout.ChromeNavButtonW : 0f)
         + 2f * ShellResponsiveLayout.ChromeGutterMinW
         + ShellResponsiveLayout.ChromeMinDragStripW
         + ShellResponsiveLayout.ChromeCaptionClusterW;

    readonly record struct Stage(
        bool Name, bool Friends, bool Forward, bool Back, bool NewTab, bool Trailing, bool Field);

    static Stage StageFor(float width, float naturalTabExtent)
    {
        bool name = width >= ShellResponsiveLayout.ChromeNameEnterW;
        bool friends = width >= ShellResponsiveLayout.ChromeFriendsEnterW;
        bool forward = width > ShellResponsiveLayout.ChromeForwardEnterW;
        bool back = true, newTab = true, trailing = true;

        // The tab viewport is the last elastic lane. Under extreme pressure shed fixed islands before allowing it to
        // disappear; captions and the compact search trigger never participate in that trade.
        bool FitsEssential()
            => width - FixedBudget(name, friends, forward, back, newTab, trailing)
                     - ShellResponsiveLayout.ChromeSearchIconW
               >= ShellResponsiveLayout.ChromeTabViewportMinW;
        if (!FitsEssential()) newTab = false;
        if (!FitsEssential()) trailing = false;
        if (!FitsEssential()) back = false;

        float search = PreferredSearchWidth(width);
        float tabComfort = ComfortableTabExtent(naturalTabExtent);
        float tabLaneWithField = width
            - FixedBudget(name, friends, forward, back, newTab, trailing)
            - search;
        bool field = tabLaneWithField >= tabComfort;
        return new Stage(name, friends, forward, back, newTab, trailing, field);
    }

    static MergedChromeLayout Compose(float width, in Stage stage)
        => new(stage.Name, stage.Friends, stage.Forward, stage.Back, stage.NewTab, stage.Trailing,
            stage.Field ? MergedSearchMode.Field : MergedSearchMode.Icon,
            stage.Field ? PreferredSearchWidth(width) : ShellResponsiveLayout.ChromeSearchIconW);

    static float QuantiseDown(float value)
        => MathF.Floor(value / ShellResponsiveLayout.ChromeWidthQuantumW)
         * ShellResponsiveLayout.ChromeWidthQuantumW;

    static float QuantiseUp(float value)
        => MathF.Ceiling(value / ShellResponsiveLayout.ChromeWidthQuantumW)
         * ShellResponsiveLayout.ChromeWidthQuantumW;
}
