using Wavee.Core;

namespace Wavee;

/// <summary>
/// Which <see cref="IAppUpdateService"/> a BUTTON should talk to.
///
/// <para>Normally the answer is the one real updater on the service bag. While the developer-mode simulator
/// (<see cref="FakeAppUpdateService"/>) is walking the lifecycle, the answer is the simulator — because the rows,
/// toasts and About cards on screen were published BY the simulation, and wiring their "Update now" / "Later" /
/// "Retry" to the live service made them inert: the real updater is Idle, so <c>ApplyAsync</c> returned immediately
/// and the simulated card sat there doing nothing. A simulation whose buttons do not work exercises the half of the
/// UI that matters least.</para>
///
/// <para>One helper, three call sites (the notification-centre bridge's toast actions, the notification panel's update
/// row, Settings › About's primary button and status card). Resolving this per call site is exactly how the three
/// drifted apart the first time.</para>
/// </summary>
static class AppUpdateSurface
{
    /// <summary>The updater the UI should drive: the running simulation if there is one, else the live service.</summary>
    public static IAppUpdateService? Resolve(Services? services) => Resolve(services?.AppUpdate);

    /// <summary>The updater the UI should drive, given whatever live service the caller already holds.</summary>
    public static IAppUpdateService? Resolve(IAppUpdateService? live)
        => FakeAppUpdateService.Active as IAppUpdateService ?? live;

    /// <summary>True while a simulated walk is running. Surfaces that DISABLE themselves on a dev build (Settings ›
    /// About's primary button, its state pill) must not do so during a simulation — a greyed-out "Check for updates"
    /// is just as inert as a button wired to the wrong service, and the simulation exists precisely because a dev
    /// build can never reach these states any other way.</summary>
    public static bool IsSimulating => FakeAppUpdateService.Active is not null;
}
