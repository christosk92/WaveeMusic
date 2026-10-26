// ── Shell/UpdateToastModel.cs — the update lifecycle toast's state and the update action verb ──────────────────────
//
// Role: CORE (themes plan §5.1, PR C8; extraction X22). The toast watcher in `Shell.Overlays.UI.cs` used to hold the
// previous snapshot and the progress cell as statics of a component class and decide inside its effect. They are one model
// now: the watcher feeds it each published snapshot and shows what it answers.
//
// ONE CARD FOR THE WHOLE LIFECYCLE (dedupe key "update"): a plan only when the state MOVED; the download card mounted once
// with its bar BOUND to <see cref="Progress"/>, so twenty progress ticks are twenty float writes, not twenty reconciles. The
// progress is written BEFORE the plan early-out and before the dial check, so a card that appears mid-download starts at the
// right percentage.

using FluentGpu.Signals;

namespace Wavee;

/// <summary>The update toast's state: the last snapshot seen and the bound progress value.</summary>
public sealed class UpdateToastModel
{
    /// <summary>The toast's dedupe key — one card, replaced in place as the lifecycle moves.</summary>
    public const string ToastKey = "update";

    /// <summary>The auto-dismiss of a non-sticky update toast (ms).</summary>
    public const float AutoDismissMs = 5000f;

    AppUpdateSnapshot _previous = AppUpdateSnapshot.Idle;

    /// <summary>0..1, bound to the download card's bar.</summary>
    public FloatSignal Progress { get; } = new(0f);

    /// <summary>The process-wide model the overlay lane's watcher feeds.</summary>
    public static UpdateToastModel Shared { get; } = new();

    /// <summary>Take the next published snapshot: remember it, write the progress, and answer the toast to raise (null when
    /// the state did not move, or the App-updates dial is Off). <paramref name="appUpdatesLevel"/> is asked only when a plan
    /// exists, so a quiet transition never reads the dial.</summary>
    public ToastPlan? Advance(AppUpdateSnapshot next, Func<NotifyLevel> appUpdatesLevel)
    {
        var previous = _previous;
        _previous = next;
        Progress.Value = Math.Clamp(next.ProgressPercent / 100f, 0f, 1f);
        if (Notify.AppUpdateToasts.Plan(previous, next) is not { } plan) return null;
        return OverlayGates.ShowsUpdateToast(appUpdatesLevel()) ? plan : null;
    }

    /// <summary>An update action from a toast or the panel: What's new navigates; everything else routes through the SAME
    /// <see cref="Notify.UpdateCommand"/> door the notification-centre row uses (G-092), via <see cref="Notify.VerbFor"/> —
    /// a toast offers one <see cref="ToastActionKind"/>, the row offers an <see cref="UpdateRowAction"/>, and only one of the
    /// two needs an update host behind it. <paramref name="openWhatsNew"/> receives the version whose notes to open.</summary>
    public static void RunAction(ToastActionKind kind, AppUpdateSnapshot snapshot, Action<string> openWhatsNew)
    {
        if (kind != ToastActionKind.WhatsNew) { Notify.UpdateCommand?.Invoke(Notify.VerbFor(kind), snapshot); return; }
        openWhatsNew(WhatsNewVersion(snapshot));
    }

    /// <summary>The version a toast's What's-new opens: the target semver when known, else the release tag of the target quad.</summary>
    public static string WhatsNewVersion(AppUpdateSnapshot snapshot)
        => snapshot.TargetSemVer is { Length: > 0 } semver
            ? semver
            : Notify.AppUpdateVersion.ReleaseTagVersion(snapshot.TargetQuad);
}
