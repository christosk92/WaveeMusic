// ── Theming/Policies/DetailPolicy.cs — what a structure decides about the detail pages (themes plan 5.5, M8) ──────────────
//
// The detail frame's STATE MACHINE is core (`Detail.FrameHost`: the mode ladder, the rail cells, the grip, the persistence of each rail's width and
// collapse). Two choices about the page's SHAPE are the structure's: whether a track page composes the rail beside its list at all (the page
// layout), and whether every kind shares one rail size (the rail's uniformity). A structure that offers them publishes them here; one that does not
// is the shell as it shipped before themes (`Default`: Automatic, one remembered size per kind).

namespace Wavee.Theming;

/// <summary>The id of the theme verb a structure's "Clear all remembered sizes" action names (the core registers the behaviour, `DetailRailPrefs.RegisterVerbs`).</summary>
public static class DetailVerbIds
{
    /// <summary>Forget every kind's remembered rail width and collapse (never the shared uniform one).</summary>
    public const string ClearRememberedSizes = "clearRememberedSizes";
}

/// <summary>The two page layouts of a track page. The values are what is stored (appended members never re-mean a stored value).</summary>
public enum DetailPageLayout : byte
{
    /// <summary>The rail on wide windows, the hero on narrow ones.</summary>
    Automatic = 0,

    /// <summary>The hero composition at every width (no rail).</summary>
    Hero = 1,
}

/// <summary>What the active structure decides for the detail pages.</summary>
/// <param name="PageLayout">Which composition a TRACK page uses (an episode list always follows the width).</param>
/// <param name="RailUniform">"Keep the left rail the same size": every kind's rail rests at one shared remembered size
/// (<see cref="RailScope.Uniform"/>). Meaningful only with <see cref="DetailPageLayout.Automatic"/>: the hero composes no rail to size.</param>
public sealed record DetailPolicy(DetailPageLayout PageLayout, bool RailUniform)
{
    /// <summary>The shell as it shipped before themes: Automatic, one remembered size per kind.</summary>
    public static DetailPolicy Default { get; } = new(DetailPageLayout.Automatic, RailUniform: false);
}
