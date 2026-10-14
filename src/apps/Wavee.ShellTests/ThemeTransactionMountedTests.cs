// ── Wavee.ShellTests/ThemeTransactionMountedTests.cs — theme switches on the engine's theme transaction, mounted (A4.2) ──────
//
// `ThemeHost.Switch` and Transaction-mode `ThemeHost.Apply` run on the engine's theme transaction (EP14, E5). What only the real
// mounted frame can say:
//
//   live        the test-only ClassicAlt theme (Classic's structure, other geometry) switches live: the window freezes, the root
//               remounts under the frozen frame, the settle ends quiet, the thaw dissolves; and back to Classic the same way
//   in place    a Transaction-mode option is published in a transaction WITHOUT a remount: the root and its scope stay, the page's
//               state stays, the new definition is installed
//   navigation  a navigation asked while a transaction runs waits for its thaw start, then lands
//   the gate    100 switches over Home, Artist, Search, Concert, Browse, Show and the stage with lyrics playing and the visualizer up:
//               every one commits and none ends in a settle timeout (the plan's R2-B2 gate on the real frame); latency is recorded
//
// The installed themes and `theme.id` are process state: every fact puts Classic back.

using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using Wavee;
using Wavee.Theming;
using Wavee.Themes.Classic;
using Xunit;
using static Wavee.ShellTests.ShellProbe;

namespace Wavee.ShellTests;

public sealed class ThemeTransactionMountedTests(ShellHost host, ITestOutputHelper output)
{
    static readonly SettingKey<string> IdKey = new("theme.id", "classic");

    /// <summary>Installs Classic and ClassicAlt, mounts, runs <paramref name="body"/>, and puts Classic alone back.</summary>
    void WithAlt(Action<Shell.HeadlessShell> body)
    {
        host.Run(static () => ThemeHost.Install(ClassicTheme.Create(), ClassicAlt.Create()));
        try { host.Mounted(body); }
        finally { host.Run(ClassicAlt.RestoreClassic); }
    }

    /// <summary>Run frames until <paramref name="t"/> completes, noting whether the window was frozen on any of them.</summary>
    static ThemeTransactionResult Complete(Shell.HeadlessShell shell, Task<ThemeTransactionResult> t, out bool sawFreeze, int maxFrames = 300)
    {
        sawFreeze = false;
        int painted = 0;
        Action<FrameStats> count = _ => painted++;
        shell.Host.FrameCompleted += count;
        for (int i = 0; i < maxFrames && !t.IsCompleted; i++)
        {
            shell.RunFrames(1);
            sawFreeze |= shell.Host.IsFrozen;
        }
        shell.Host.FrameCompleted -= count;
        Assert.True(t.IsCompletedSuccessfully, "the transaction completed within " + maxFrames + " frames (freeze=" + shell.Host.FreezeState + " settling=" + shell.Host.IsSettling + " snapping=" + shell.Host.IsSnapping + " running=" + shell.Host.IsThemeTransactionRunning + " busy=" + shell.Host.ThemeTransactionBusy.Peek() + " visible=" + shell.Window.IsVisible + " parked=" + shell.Host.IsParked + " wake=" + shell.Host.CurrentWakeReasons + " painted=" + painted + ")");
        return t.Result;
    }

    static void Drain(Shell.HeadlessShell shell)
    {
        for (int i = 0; i < 120 && shell.Host.FreezeState != FreezePhase.None; i++) shell.RunFrames(1);
        shell.RunFrames(2);
    }

    // ── live ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ClassicAlt_switches_live_on_a_transaction_and_back()
        => WithAlt(shell =>
        {
            shell.RunFrames(10);
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
            Until(shell, () => !shell.Find("home:facet:band").IsNull, "the home page");
            shell.RunFrames(30);
            long epoch = ThemeHost.RootEpoch.Peek();
            var scope = RootScope.Current;

            var r = Complete(shell, ThemeHost.Switch(ClassicAlt.Id), out bool froze);
            Drain(shell);
            Assert.Equal(ThemeTransactionOutcome.Committed, r.Outcome);
            Assert.True(froze, "the window was frozen while the new root mounted");
            Assert.NotNull(r.Settle);
            Assert.False(r.Settle!.TimedOut, "settle-timeout: " + string.Join(",", r.Settle.UnsettledKeys));
            Assert.Same(ClassicAlt.Definition, Tok.Definition);
            Assert.Equal(ClassicAlt.Id, ThemeHost.Current.Peek().Manifest.Id);
            Assert.Equal(ClassicAlt.Id, Platform.Settings.Get(IdKey));
            Assert.Equal(epoch + 1, ThemeHost.RootEpoch.Peek());
            Assert.NotSame(scope, RootScope.Current);                       // the root remounted (owner Q26: nothing carried)
            Assert.Equal(Shell.RouteKind.Home, Shell.Current.Peek().Kind);   // the route is core state
            Assert.False(shell.Find("home:facet:band").IsNull, "the new root shows the same page");
            Assert.Null(ThemeHost.PendingSwitch.Peek());
            output.WriteLine($"to ClassicAlt: latency={r.LatencyMs:F1} ms passes={r.Settle.Passes}");

            var back = Complete(shell, ThemeHost.Switch(ClassicTheme.Id), out bool frozeBack);
            Drain(shell);
            Assert.Equal(ThemeTransactionOutcome.Committed, back.Outcome);
            Assert.True(frozeBack);
            Assert.Same(ClassicAlt.ClassicDefinition, Tok.Definition);
            Assert.Equal(ClassicTheme.Id, Platform.Settings.Get(IdKey));
            ShellHost.AssertNoNetwork();
        });

    [Fact]
    public void A_switch_to_the_active_theme_does_nothing()
        => WithAlt(shell =>
        {
            shell.RunFrames(10);
            long epoch = ThemeHost.RootEpoch.Peek();
            var t = ThemeHost.Switch(ClassicTheme.Id);
            Assert.True(t.IsCompleted);
            shell.RunFrames(3);
            Assert.False(shell.Host.IsFrozen);
            Assert.Equal(epoch, ThemeHost.RootEpoch.Peek());
        });

    // ── in place ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_transaction_option_applies_in_place_and_keeps_the_page()
        => WithAlt(shell =>
        {
            shell.RunFrames(10);
            Complete(shell, ThemeHost.Switch(ClassicAlt.Id), out _);
            Drain(shell);
            Settings.Open(Settings.Tab.Appearance);
            Until(shell, () => SettingsContext.Overlay is not null, "the Settings page");
            shell.RunFrames(20);
            long epoch = ThemeHost.RootEpoch.Peek();
            var scope = RootScope.Current;
            var overlay = SettingsContext.Overlay;

            var t = ThemeHost.Apply(new OptionChange(ClassicAlt.Id, ClassicAlt.SquareCornersQualified, true));
            var r = Complete(shell, (Task<ThemeTransactionResult>)t, out bool froze);
            Drain(shell);
            Assert.Equal(ThemeTransactionOutcome.Committed, r.Outcome);
            Assert.True(froze, "a Transaction-mode option freezes like a switch");
            Assert.Same(ClassicAlt.SquareDefinition, Tok.Definition);
            Assert.Equal(epoch, ThemeHost.RootEpoch.Peek());   // no remount
            Assert.Same(scope, RootScope.Current);
            Assert.Same(overlay, SettingsContext.Overlay);       // the page's own instance state survived (plan 5.6)
            Assert.Equal(Shell.RouteKind.Settings, Shell.Current.Peek().Kind);

            // Back, and the same value again is no transaction at all.
            Complete(shell, (Task<ThemeTransactionResult>)ThemeHost.Apply(new OptionChange(ClassicAlt.Id, ClassicAlt.SquareCornersQualified, false)), out _);
            Drain(shell);
            Assert.Same(ClassicAlt.Definition, Tok.Definition);
            var same = ThemeHost.Apply(new OptionChange(ClassicAlt.Id, ClassicAlt.SquareCornersQualified, false));
            Assert.True(same.IsCompleted);
            shell.RunFrames(2);
            Assert.False(shell.Host.IsFrozen);
        });

    // ── navigation ───────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_navigation_asked_during_a_transaction_waits_for_its_thaw_start()
        => WithAlt(shell =>
        {
            shell.RunFrames(10);
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Home));
            shell.RunFrames(10);
            var t = ThemeHost.Switch(ClassicAlt.Id);
            Assert.True(ThemeHost.IsTransactionRunning);
            Shell.GoTo(new Shell.Route(Shell.RouteKind.Liked));
            Assert.Equal(Shell.RouteKind.Home, Shell.Current.Peek().Kind);   // held
            var r = Complete(shell, t, out _);
            Assert.Equal(ThemeTransactionOutcome.Committed, r.Outcome);
            Assert.Equal(Shell.RouteKind.Liked, Shell.Current.Peek().Kind);  // landed at thaw start
            Drain(shell);
            Assert.False(ThemeHost.IsTransactionRunning);
        });

    // ── the gate ─────────────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_hundred_switches_over_the_main_pages_and_the_stage_never_time_out_their_settle()
        => WithAlt(shell =>
        {
            Shell.Route[] pages =
            [
                new(Shell.RouteKind.Home),
                Shell.For(EntityUri.Parse("spotify:artist:ar0")),
                Shell.Parse("search", "queen"),
                Shell.Parse("concert:cf0"),
                new(Shell.RouteKind.Browse),
                Shell.For(EntityUri.Parse("spotify:show:sh0")),
            ];
            int stagePrefMode = Platform.Settings.Get(Platform.Keys.StageMode);
            var latencies = new List<double>();
            var failures = new List<string>();
            int n = 0;
            try
            {
                shell.RunFrames(10);
                // 14 on each page, then 16 on the stage with lyrics playing and the visualizer up: 100 in all.
                foreach (var page in pages)
                {
                    Shell.GoTo(page);
                    shell.RunFrames(40);
                    for (int i = 0; i < 14; i++) Switch();
                }
                Play("spotify:track:tr40", fromMs: 20_000);
                if (!(Shell.Ui.RailOpen.Peek() && Shell.Ui.Mode.Peek() == Shell.RailMode.Lyrics)) Shell.Ui.Toggle(Shell.RailMode.Lyrics);
                Prefs.Stage.SetMode((int)Stage.Mode.Visualizer);
                Stage.Open(null, "test");
                Until(shell, () => !shell.Find("stage:cover").IsNull, "the stage");
                shell.RunFrames(30);
                for (int i = 0; i < 16; i++) Switch();
            }
            finally
            {
                Stage.Close(null, "test");
                if (Shell.Ui.RailOpen.Peek() && Shell.Ui.Mode.Peek() == Shell.RailMode.Lyrics) Shell.Ui.Toggle(Shell.RailMode.Lyrics);
                Prefs.Stage.SetMode(stagePrefMode);
                shell.RunFrames(20);
            }
            latencies.Sort();
            output.WriteLine($"{n} switches: latency p50={latencies[latencies.Count / 2]:F1} ms p90={latencies[latencies.Count * 9 / 10]:F1} ms max={latencies[^1]:F1} ms");
            Assert.Equal(100, n);
            Assert.True(failures.Count == 0, string.Join("; ", failures));

            void Switch()
            {
                n++;
                string to = ThemeHost.Current.Peek().Manifest.Id == ClassicAlt.Id ? ClassicTheme.Id : ClassicAlt.Id;
                var r = Complete(shell, ThemeHost.Switch(to), out _);
                Drain(shell);
                latencies.Add(r.LatencyMs);
                if (r.Outcome != ThemeTransactionOutcome.Committed) failures.Add($"#{n} {Shell.Current.Peek().Kind}: {r.Outcome}");
                else if (r.Settle is { TimedOut: true } s) failures.Add($"#{n} {Shell.Current.Peek().Kind}: settle-timeout {string.Join(",", s.UnsettledKeys)}");
            }
        });
}
