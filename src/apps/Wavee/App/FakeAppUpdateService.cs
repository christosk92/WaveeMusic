using System;
using System.Threading;
using System.Threading.Tasks;
using Wavee.Core;

namespace Wavee;

/// <summary>
/// The developer-mode update simulator (Settings › Developer ▸ "Simulate update"). It walks the WHOLE update
/// lifecycle — Checking → Available → Downloading 0…100 → Installing → Completed — pushing each snapshot through
/// <see cref="NotificationCenterBridge.Simulate"/>, i.e. down the exact same path a real observation takes: the same
/// merge, the same topic filter, the same toast plan, the same OS escalation.
/// <para>
/// It does NOT replace <c>Services.AppUpdate</c>. The real service stays wired and stays authoritative; this only
/// injects rows. That matters because the states this walks through are otherwise unreachable without a signed
/// release on a live feed, and every one of them has UI attached to it (a sticky progress toast, a restart notice, a
/// success card, an OS toast with a data-bound bar) that would otherwise ship untested.
/// </para>
/// <para>It is also a real <see cref="IAppUpdateService"/>, so a surface can bind its buttons to the simulation
/// instead of the live updater while a walk is running.</para>
/// </summary>
sealed class FakeAppUpdateService : IAppUpdateService
{
    /// <summary>The simulated quad/semver/codename. Deliberately implausible so a screenshot of a simulation is never
    /// mistaken for a real release.</summary>
    const string SimQuad = "99.9.9.999";
    const string SimSemVer = "99.9.9";
    const string SimCodename = "Simulated";

    static readonly TimeSpan CheckingDelay = TimeSpan.FromMilliseconds(600);
    static readonly TimeSpan AvailableDelay = TimeSpan.FromMilliseconds(1500);
    static readonly TimeSpan ProgressStep = TimeSpan.FromMilliseconds(150);
    static readonly TimeSpan InstallingDelay = TimeSpan.FromMilliseconds(900);
    const int ProgressIncrement = 5;

    readonly SimpleEvent<int> _changed = new();
    readonly NotificationCenterBridge _bridge;
    readonly CancellationTokenSource _cts = new();
    int _rev;

    /// <summary>The walk currently running, or null. A second <see cref="Start"/> cancels the first.</summary>
    public static FakeAppUpdateService? Active { get; private set; }

    public AppUpdateSnapshot Current { get; private set; } = AppUpdateSnapshot.Idle;
    public IObservable<int> Changed => _changed;
    public string FeedUrl => "simulated://wavee/update";

    FakeAppUpdateService(NotificationCenterBridge bridge) => _bridge = bridge;

    /// <summary>Start (or restart) the simulated walk against <paramref name="bridge"/>. Returns immediately; the
    /// walk runs on the thread pool and marshals every publish onto the UI thread through the bridge.</summary>
    public static FakeAppUpdateService Start(NotificationCenterBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        Active?.Stop();
        var svc = new FakeAppUpdateService(bridge);
        Active = svc;
        _ = Task.Run(() => svc.RunAsync(svc._cts.Token));
        return svc;
    }

    /// <summary>Cancel a running walk. The rows already injected stay in the centre until the user clears them.</summary>
    public void Stop()
    {
        try { _cts.Cancel(); } catch (Exception) { }
        if (ReferenceEquals(Active, this)) Active = null;
    }

    async Task RunAsync(CancellationToken ct)
    {
        try
        {
            Publish(AppUpdateSnapshot.Idle with { State = AppUpdateState.Checking });
            await Task.Delay(CheckingDelay, ct).ConfigureAwait(false);

            Publish(Available());
            await Task.Delay(AvailableDelay, ct).ConfigureAwait(false);

            await ApplyAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        finally { if (ReferenceEquals(Active, this)) Active = null; }
    }

    public Task CheckAsync(UpdateCheckOrigin origin, CancellationToken ct)
    {
        Publish(Available());
        return Task.CompletedTask;
    }

    /// <summary>The download → install → "you were updated" half of the walk. Reachable on its own so an "Update now"
    /// press on a simulated row does what the real one does.</summary>
    public async Task ApplyAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var token = linked.Token;
        try
        {
            for (int pct = 0; pct <= 100; pct += ProgressIncrement)
            {
                Publish(Available() with { State = AppUpdateState.Downloading, ProgressPercent = pct });
                await Task.Delay(ProgressStep, token).ConfigureAwait(false);
            }
            Publish(Available() with { State = AppUpdateState.Installing, ProgressPercent = 100 });
            await Task.Delay(InstallingDelay, token).ConfigureAwait(false);

            // The real Completed is raised by the NEXT process's ctor, against the version that just installed — so the
            // simulated one names the simulated build, not the target.
            Publish(new AppUpdateSnapshot(AppUpdateState.Completed, SimQuad, SimSemVer, SimCodename, 0, null,
                AutoUpdateAssociated: true, LastCheckedMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        }
        catch (OperationCanceledException) { }
    }

    public void Snooze()
    {
        if (Current.State is AppUpdateState.Available) Publish(Current with { State = AppUpdateState.Snoozed });
    }

    public void Acknowledge()
    {
        Publish(AppUpdateSnapshot.Idle);
        _bridge.ClearSimulated();
        Stop();
    }

    static AppUpdateSnapshot Available() => new(AppUpdateState.Available, SimQuad, SimSemVer, SimCodename, 0, null,
        AutoUpdateAssociated: true, LastCheckedMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    void Publish(AppUpdateSnapshot snapshot)
    {
        Current = snapshot;
        _changed.OnNext(Interlocked.Increment(ref _rev));
        long ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _bridge.Post(() => _bridge.Simulate(new AppUpdateNotification(ts, IsUnread: true, snapshot)));
    }
}
