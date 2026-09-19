using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wavee.Core;

/// <summary>The app-update lifecycle. <see cref="None"/> = nothing pending; <see cref="Snoozed"/> = an update the user
/// pressed "Later" on (still available, no longer shouting); <see cref="Completed"/> = this launch followed an update.</summary>
public enum AppUpdateState { None, Checking, Available, Snoozed, Downloading, Installing, Completed, Failed }

/// <summary>Why an update attempt failed, in the terms the UI needs to offer a next step (retry, unmeter, close the
/// helper processes, open the release page). Maps from the deployment HRESULT by <c>PackageUpdater.Classify</c>.</summary>
/// <summary>Who asked for a feed check. See <see cref="IAppUpdateService.CheckAsync"/>.</summary>
public enum UpdateCheckOrigin : byte { Scheduled, User }

public enum AppUpdateFailureKind { Network, Metered, PackagesInUse, VersionConflict, SideloadPolicy, AppInstallerOutdated, NotAssociated, Unknown }

/// <summary>One failure, carrying the raw HRESULT for diagnostics alongside the classified kind.</summary>
public sealed record AppUpdateFailure(AppUpdateFailureKind Kind, int HResult, string Message);

/// <summary>One immutable observation. Published whole so the UI never reads a torn state.</summary>
/// <param name="State">Where the update lifecycle stands.</param>
/// <param name="TargetQuad">Feed root Version while Available/Snoozed/Downloading/Installing; the running quad when Completed.</param>
/// <param name="TargetSemVer">From whatsnew-index when known ("0.3.0").</param>
/// <param name="TargetCodename">"Crest" when known.</param>
/// <param name="ProgressPercent">0..100 while Downloading/Installing.</param>
/// <param name="Failure">Set only in <see cref="AppUpdateState.Failed"/>.</param>
/// <param name="AutoUpdateAssociated"><c>GetAppInstallerInfo() != null</c> — the OS knows the feed (packaged only).</param>
/// <param name="LastCheckedMs">Unix-ms of the last completed feed check, 0 when never.</param>
/// <param name="Quiet">A <see cref="AppUpdateState.Failed"/> that must not raise a toast: a SCHEDULED check that could not
/// reach the feed. The failure is still the state (Settings › About shows it, with Retry); only the interruption is
/// withheld — a background poll on a flaky link, or a first launch before the feed exists, is not the user's problem
/// to dismiss. A user-initiated check or any apply failure is never quiet.</param>
public sealed record AppUpdateSnapshot(
    AppUpdateState State,
    string? TargetQuad,
    string? TargetSemVer,
    string? TargetCodename,
    int ProgressPercent,
    AppUpdateFailure? Failure,
    bool AutoUpdateAssociated,
    long LastCheckedMs,
    bool Quiet = false)
{
    /// <summary>Nothing pending, nothing known — the state every process starts in.</summary>
    public static readonly AppUpdateSnapshot Idle = new(AppUpdateState.None, null, null, null, 0, null, false, 0);
}

/// <summary>The app-update seam. App-scoped (one process-wide updater) → NO switchable wrapper; a plain field on Services.
/// <para><see cref="Current"/> is the whole observation; <see cref="Changed"/> ticks a revision so readers re-read it.</para></summary>
public interface IAppUpdateService
{
    AppUpdateSnapshot Current { get; }

    /// <summary>Revision ticks; readers re-read <see cref="Current"/>.</summary>
    IObservable<int> Changed { get; }

    /// <summary>The .appinstaller URL this build polls (channel + arch + feed release baked in).</summary>
    string FeedUrl { get; }

    /// <summary>Poll the feed. <paramref name="origin"/> decides how a failure SURFACES, never whether it is recorded:
    /// a <see cref="UpdateCheckOrigin.Scheduled"/> poll that cannot reach the feed publishes a quiet
    /// <see cref="AppUpdateState.Failed"/> (no toast), a <see cref="UpdateCheckOrigin.User"/> check a loud one.</summary>
    Task CheckAsync(UpdateCheckOrigin origin, CancellationToken ct);

    /// <summary>"Update now": download + stage + restart (packaged) / open the release page (unpackaged).</summary>
    Task ApplyAsync(CancellationToken ct);

    /// <summary>"Later": Available → Snoozed for this <see cref="AppUpdateSnapshot.TargetQuad"/>.</summary>
    void Snooze();

    /// <summary>Clears a Completed/Failed observation (the user saw it) — never fired on panel open.</summary>
    void Acknowledge();
}

/// <summary>The default (no updater wired): permanently <see cref="AppUpdateSnapshot.Idle"/>, every action inert.</summary>
public sealed class NullAppUpdateService : IAppUpdateService
{
    readonly SimpleEvent<int> _changed = new();
    public AppUpdateSnapshot Current => AppUpdateSnapshot.Idle;
    public IObservable<int> Changed => _changed;
    public string FeedUrl => "";
    public Task CheckAsync(UpdateCheckOrigin origin, CancellationToken ct) => Task.CompletedTask;
    public Task ApplyAsync(CancellationToken ct) => Task.CompletedTask;
    public void Snooze() { }
    public void Acknowledge() { }
}
