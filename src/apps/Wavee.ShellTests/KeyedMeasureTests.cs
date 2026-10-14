// ── Wavee.ShellTests/KeyedMeasureTests.cs — keyed measurement and the settle on the real frame (A4.0, E17c) ─────────────────
//
// A4.0 adopts the engine's keyed measurement (EP11): the content host provides each page slot's identity as the
// `MeasureMemory.Scope`, and the page's measured regions (`Responsive.Of(key, ...)`, `UseMeasuredWidth(key)`,
// `UseMeasuredBounds(key)`) key themselves by their role inside it. What only the mounted frame can say:
//
//   scope        a page's regions are remembered under `Shell.MeasureScopeOf(route)` + their role, so the same page instance
//                remounting (a theme switch's new root, a revisit) starts at its laid-out size
//   remount      the remounted frame finds the remembered sizes again: the same page in a NEW mount reads the same keys
//   settle       a settle on the mounted shell, inside a snap scope, ends quiet within its cap (the EP14 gate's precondition)

using FluentGpu.Hooks;
using FluentGpu.Hosting;
using Wavee;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class KeyedMeasureTests(ShellHost host)
{
    static readonly Shell.Route s_home = new(Shell.RouteKind.Home);

    static SettleResult Done(Task<SettleResult> t) => t.GetAwaiter().GetResult();   // already completed: no blocking wait

    [Fact]
    public void The_scope_is_the_page_slot_and_differs_per_tab()
    {
        var a = new Shell.Route(Shell.RouteKind.Home, Tab: 0);
        var b = new Shell.Route(Shell.RouteKind.Home, Tab: 1);
        Assert.Equal(Shell.SlotKey(a) + "/", Shell.MeasureScopeOf(a));
        Assert.NotEqual(Shell.MeasureScopeOf(a), Shell.MeasureScopeOf(b));
    }

    [Fact]
    public void Home_regions_are_remembered_under_the_page_scope_and_found_again_by_a_new_mount()
    {
        MeasureMemory.Clear();
        string scope = "";
        float facetW = 0f;
        host.Mounted(shell =>
        {
            shell.RunFrames(5);
            Shell.GoTo(s_home);
            Until(shell, () => !shell.Find("home:facet:band").IsNull, "the home facet band");
            shell.RunFrames(20);
            scope = Shell.MeasureScopeOf(Shell.Current.Peek());
            Assert.True(MeasureMemory.TryGetSize(scope + "home:facet", out var size), "the facet row is remembered under " + scope);
            Assert.True(size.Width > 0f);
            facetW = size.Width;
        });

        host.Mounted(shell =>
        {
            shell.RunFrames(5);
            Shell.GoTo(s_home);
            Until(shell, () => !shell.Find("home:facet:band").IsNull, "the home facet band");
            shell.RunFrames(20);
            Assert.Equal(scope, Shell.MeasureScopeOf(Shell.Current.Peek()));
            Assert.True(MeasureMemory.TryGetSize(scope + "home:facet", out var again));
            Assert.Equal(facetW, again.Width);   // the same slot at the same window size: the remembered width was the final one
        });
    }

    [Fact]
    public void A_settle_on_the_mounted_shell_ends_quiet_within_its_cap()
        => host.Mounted(shell =>
        {
            shell.RunFrames(5);
            Shell.GoTo(s_home);
            Until(shell, () => !shell.Find("home:facet:band").IsNull, "the home facet band");
            shell.RunFrames(30);
            using var snap = SnapScope.Enter(shell.Host);
            var settle = shell.Host.Settle();
            for (int i = 0; i < 64 && !settle.IsCompleted; i++) shell.Host.RunFrame();
            Assert.True(settle.IsCompletedSuccessfully, "the settle ended");
            var r = Done(settle);
            Assert.False(r.TimedOut, "settle-timeout: " + string.Join(",", r.UnsettledKeys));
            Assert.False(r.Cut);
        });
}
