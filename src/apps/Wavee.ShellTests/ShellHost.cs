// ── Wavee.ShellTests/ShellHost.cs — the process's one UI thread, booted once ───────────────────────────────────────
//
// xUnit runs facts on pool threads; the shell runs on the thread that booted it (`Shell.Headless`), and the DEBUG
// settings assert (`Platform.UiThreadId`) tears the whole testhost down on a write from any other. So the assembly
// fixture owns one dedicated thread: it boots the host there, and every fact marshals its body onto it with `Run`.
// Facts never run in parallel (assembly attribute below): one mount at a time is the host's own rule.
//
// ISOLATION. xUnit v3 runs the classes in a different order on every run, and the host is ONE process: whatever a fact
// leaves in process state is what the next fact starts from. `FactIsolation` (assembly attribute below) puts back,
// before every fact, the process state a fact cannot be expected to restore because production keeps it on purpose:
// the navigation (a remount opens on the page the last root showed), the playback deck (a stop keeps the stopped row, whose
// face, lyrics and video split the next fact's bar would draw), the window tiers (`ThemeHost.Facts.Tiers` is the
// last mounted window's until the next root resolves its own) and, as a guard, Classic's option values.
//
// THE BOOT LOG. The app log is a 4096-entry ring and a full run writes well over that, so the boot's own lines are gone
// long before the last fact: the host keeps the ring as the boot left it (`BootLog`).

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Wavee;
using Wavee.Themes.Classic;
using Wavee.Theming;
using Wavee.ShellTests;
using Xunit;

[assembly: AssemblyFixture(typeof(ShellHost))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]
[assembly: FactIsolation]

namespace Wavee.ShellTests;

public sealed class ShellHost : IDisposable
{
    readonly BlockingCollection<Action> _work = new();
    readonly Thread _thread;

    /// <summary>The temporary profile the host booted against.</summary>
    public string ProfileDir { get; } = Path.Combine(Path.GetTempPath(), "wavee-shell-tests", Guid.NewGuid().ToString("n"));

    /// <summary>The assembly fixture, once constructed (<see cref="FactIsolationAttribute"/> reaches the UI thread through it).</summary>
    internal static ShellHost? Instance { get; private set; }

    /// <summary>Classic's option values as the boot left them (the stamps of <see cref="ThemeHost.Values"/>).</summary>
    (int Structure, int Style, int Composition) _bootOptions;

    /// <summary>The window tiers as the boot left them (no window mounted yet).</summary>
    TierFacts _bootTiers;

    /// <summary>The app log as the boot left it, oldest first: what the ring held before the first fact ran.</summary>
    public WaveeLogEntry[] BootLog { get; private set; } = [];

    public ShellHost()
    {
        _thread = new Thread(Loop) { Name = "wavee-shell-ui", IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        Run(() =>
        {
            Shell.Headless.Boot(new Shell.ShellHostOptions(ProfileDir));
            // The network grader (Palette.Filler) is null under --fake, so no cover is ever graded and no surface that
            // colours itself from artwork (the shell washes, the tints) would exist. The fixture grades every cover one
            // colour, from the first frame on: a cover asked while no grader exists is marked failed for good.
            Diagnostics.InspectKit.InstallFixtureGrader();   // the app's own fixture grader (Screens/Diagnostics.Inspect.Kit.cs)
            _bootOptions = OptionStamps();
            _bootTiers = ThemeHost.Facts.Peek().Tiers;
            BootLog = Log.Snapshot();
        });
        Instance = this;
    }

    static (int, int, int) OptionStamps()
        => (ThemeHost.Values.Structure.Stamp, ThemeHost.Values.Style.Stamp, ThemeHost.Values.Composition.Stamp);

    /// <summary>Between facts (UI thread, nothing mounted): the navigation back to a fresh session's Home, the deck and the queue
    /// back to the boot's (nothing playing), the window tiers back
    /// to the boot's, and Classic, alone and with the options the boot left, as the active theme. Every fact that changes an
    /// option or installs a theme puts it back itself; the theme half only catches one that did not. The tiers are put back
    /// because every mount leaves its window's: a frame or an installed-theme harness republishes its own, but a view drawn
    /// under a composition (its own facts) and any code reading <c>ThemeHost.Facts</c> outside a mount would otherwise see the
    /// window of whichever fact ran before.</summary>
    internal void ResetBetweenFacts()
        => Run(() =>
        {
            Shell.Headless.ResetNavigation();
            Shell.Headless.ResetPlayback();
            if (!ThemeHost.Current.Peek().Manifest.Id.Equals(ClassicTheme.Id, StringComparison.Ordinal) || ThemeCatalog.Installed.Count != 1)
                ClassicAlt.RestoreClassic();
            if (OptionStamps() != _bootOptions) ThemeHost.ResetOptions(ClassicTheme.Id);
            ThemeHost.PublishTiers(in _bootTiers);
        });

    void Loop()
    {
        foreach (var work in _work.GetConsumingEnumerable()) work();
    }

    /// <summary>Run <paramref name="body"/> on the UI thread and wait for it; its exception is rethrown here.</summary>
    public void Run(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        using var done = new ManualResetEventSlim();
        _work.Add(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
            finally { done.Set(); }
        });
        done.Wait();
        failure?.Throw();
    }

    /// <summary>Mount the frame, run <paramref name="body"/> against it and dispose the mount, all on the UI thread. Every
    /// mount ends with the network guard: one request that left the process fails the fact that made it.</summary>
    public void Mounted(Action<Shell.HeadlessShell> body, float widthDip = 1180f, float heightDip = 760f)
        => Run(() =>
        {
            using (var shell = Shell.Headless.Mount(widthDip, heightDip)) body(shell);
            AssertNoNetwork();
        });

    /// <summary>The host refuses every request (`Wire.GoOffline`), so the `wire` log holds refusals only: any other line
    /// is a request that was sent.</summary>
    public static void AssertNoNetwork()
    {
        Assert.True(Wire.IsOffline, "the shell test host is not offline");
        foreach (var entry in Log.Snapshot())
            if (entry.Category == "wire" && !entry.Message.StartsWith("wire.refused ", StringComparison.Ordinal))
                Assert.Fail("a request reached the network: " + entry.Message);
    }

    public void Dispose()
    {
        _work.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(10));
        try { Directory.Delete(ProfileDir, recursive: true); } catch (Exception) { }
    }
}

/// <summary>Runs <see cref="ShellHost.ResetBetweenFacts"/> before every fact of the assembly.</summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class FactIsolationAttribute : Xunit.v3.BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest, Xunit.v3.IXunitTest test)
        => (ShellHost.Instance ?? throw new InvalidOperationException("The shell test host has not booted.")).ResetBetweenFacts();
}
