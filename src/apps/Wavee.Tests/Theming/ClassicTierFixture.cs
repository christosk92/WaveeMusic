// ── Wavee.Tests/Theming/ClassicTierFixture.cs — Classic's tier policy, as the shell resolves it (T3b) ─────────────────────
//
// The pure rule that used to hold its own thresholds (the sidebar's window band) now reads the active structure's window
// ladder. Its tests take the ladder from Classic's composition, the same object the shell installs, so they say what the
// frame does with Classic's numbers.

using FluentGpu.Foundation;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.Theming.Contracts;

namespace Wavee.Tests.Theming;

static class ClassicTierFixture
{
    /// <summary>Classic's tier policy, taken from its composition.</summary>
    public static TierPolicy Tiers { get; } = ClassicTheme.Create().Policies(ThemeOptionValues.Empty).Tiers;

    /// <summary>The window tier a FRESH resolve gives a window <paramref name="width"/> wide (a launch, a navigation).</summary>
    public static WidthTier FreshWindow(float width)
        => (WidthTier)Tiers.Window.Resolve(WindowTiers.UsableSize(width), -1);

    /// <summary>The window tier <paramref name="width"/> resolves to when the window was in <paramref name="from"/>.</summary>
    public static WidthTier NextWindow(float width, SidebarWindowBand from)
        => (WidthTier)Tiers.Window.Resolve(WindowTiers.UsableSize(width), (int)TierOf(from));

    /// <summary>The sidebar band <paramref name="width"/> resolves to when the window was in <paramref name="from"/>.</summary>
    public static SidebarWindowBand BandOf(float width, SidebarWindowBand from)
        => SidebarPaneModeRules.BandOf(NextWindow(width, from));

    /// <summary>The window tier that presents as <paramref name="band"/>.</summary>
    public static WidthTier TierOf(SidebarWindowBand band) => band switch
    {
        SidebarWindowBand.Tiny => WidthTier.Compact,
        SidebarWindowBand.Narrow => WidthTier.Small,
        _ => WidthTier.Standard,
    };
}
