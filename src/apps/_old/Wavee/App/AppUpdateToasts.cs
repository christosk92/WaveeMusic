using System;
using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Localization;
using Wavee.Core;

namespace Wavee;

/// <summary>What a toast may offer for an update. The bridge turns the FIRST of these into the toast's single action
/// button; the notification-centre row renders all of them.</summary>
enum ToastActionKind
{
    /// <summary>Start the download + stage + restart (packaged) / open the release page (unpackaged).</summary>
    UpdateNow,
    /// <summary>Navigate to the "What's new" page.</summary>
    WhatsNew,
    /// <summary>Snooze this exact version.</summary>
    Later,
    /// <summary>Try the failed apply again.</summary>
    Retry,
    /// <summary>Open the GitHub release page (the escape hatch when deployment cannot work here).</summary>
    OpenReleasePage,
}

/// <summary>One planned toast: already-localized text, the severity, whether it sticks, and the actions to offer.</summary>
/// <param name="Title">The toast's header line ("" when the body carries everything).</param>
/// <param name="Body">The toast's message — what <c>Toast.Show</c> takes as its first argument.</param>
/// <param name="Severity">Reuses the shared InfoBar severity palette so toast and InfoBar cannot drift.</param>
/// <param name="Sticky">True ⇒ no auto-dismiss (a download in flight, a failure the user must answer).</param>
/// <param name="Actions">Offered actions, most important first.</param>
readonly record struct ToastPlan(
    string Title,
    string Body,
    InfoBarSeverity Severity,
    bool Sticky,
    ToastActionKind[] Actions);

/// <summary>
/// The update toast decision table: <c>(previous, next)</c> snapshot → the toast to raise, or nothing.
/// <para>
/// Pure and total, and deliberately separate from the bridge that shows the card. The rule that actually matters is
/// the one a scattered set of call sites always gets wrong: an app update is <b>state</b>, not an event — its row
/// persists for as long as the update is available — so a toast is planned only when the state genuinely MOVED (or a
/// failure changed its reason). Progress ticks re-render the bar that is already on screen; they never plan a second
/// card.
/// </para>
/// <para>Checking and Snoozed are silent by design: a background poll must not interrupt, and "Later" means the user
/// already answered.</para>
/// </summary>
static class AppUpdateToasts
{
    /// <summary>Plan the toast for a transition, or null when nothing should be raised.</summary>
    /// <param name="previous">The snapshot the UI last saw (pass <see cref="AppUpdateSnapshot.Idle"/> at startup).</param>
    /// <param name="next">The snapshot just published.</param>
    public static ToastPlan? Plan(AppUpdateSnapshot previous, AppUpdateSnapshot next)
    {
        if (next is null) return null;
        bool moved = previous is null
            || previous.State != next.State
            || (next.State == AppUpdateState.Failed && previous.Failure?.Kind != next.Failure?.Kind);
        if (!moved) return null;
        // A quiet failure (a scheduled poll that could not reach the feed) is state, not an interruption.
        if (next.State == AppUpdateState.Failed && next.Quiet) return null;

        string name = ReleaseName(next);
        return next.State switch
        {
            AppUpdateState.Available => new ToastPlan(
                Strings.Update.Toast.Available(name),
                // The body names the build only when the title could not: with no codename/semver known the title
                // already IS the quad, and "Wavee 0.2.0.9002 is available / 0.2.0.9002" said it twice.
                next.TargetQuad is { Length: > 0 } quad && quad != name ? quad : "",
                InfoBarSeverity.Informational,
                Sticky: false,
                [ToastActionKind.UpdateNow, ToastActionKind.WhatsNew, ToastActionKind.Later]),

            AppUpdateState.Downloading => new ToastPlan(
                Strings.Update.Toast.Downloading(name),
                "",
                InfoBarSeverity.Informational,
                Sticky: true,
                []),

            AppUpdateState.Installing => new ToastPlan(
                "",
                Loc.Get(Strings.Update.State.Installing),
                InfoBarSeverity.Informational,
                Sticky: true,
                []),

            AppUpdateState.Completed => new ToastPlan(
                Strings.Update.Toast.Updated(name),
                "",
                InfoBarSeverity.Success,
                Sticky: false,
                [ToastActionKind.WhatsNew]),

            AppUpdateState.Failed => new ToastPlan(
                "",
                FailureText(next.Failure),
                InfoBarSeverity.Error,
                Sticky: true,
                next.Failure?.Kind == AppUpdateFailureKind.Metered
                    ? [ToastActionKind.Retry]
                    : [ToastActionKind.Retry, ToastActionKind.OpenReleasePage]),

            // None, Checking, Snoozed — nothing to say.
            _ => null,
        };
    }

    /// <summary>The button label for an action.</summary>
    public static string Label(ToastActionKind kind) => kind switch
    {
        ToastActionKind.UpdateNow => Loc.Get(Strings.Update.Action.UpdateNow),
        ToastActionKind.WhatsNew => Loc.Get(Strings.Update.Action.WhatsNew),
        ToastActionKind.Later => Loc.Get(Strings.Update.Action.Later),
        ToastActionKind.Retry => Loc.Get(Strings.Update.Action.Retry),
        ToastActionKind.OpenReleasePage => Loc.Get(Strings.Update.Action.OpenReleasePage),
        _ => "",
    };

    /// <summary>The sentence for a failure — the reason AND the next step, in one line. Shared by the toast, the
    /// notification row and Settings › About so all three tell the same story.</summary>
    public static string FailureText(AppUpdateFailure? failure)
    {
        if (failure is null) return Strings.Update.Failure.Unknown("0");
        return failure.Kind switch
        {
            AppUpdateFailureKind.PackagesInUse => Loc.Get(Strings.Update.Failure.PackagesInUse),
            AppUpdateFailureKind.VersionConflict => Loc.Get(Strings.Update.Failure.VersionConflict),
            AppUpdateFailureKind.SideloadPolicy => Loc.Get(Strings.Update.Failure.SideloadPolicy),
            AppUpdateFailureKind.Network => Loc.Get(Strings.Update.Failure.Network),
            AppUpdateFailureKind.AppInstallerOutdated => Loc.Get(Strings.Update.Failure.AppInstallerOutdated),
            AppUpdateFailureKind.Metered => Loc.Get(Strings.Update.Failure.Metered),
            AppUpdateFailureKind.NotAssociated => Loc.Get(Strings.Update.Failure.NotAssociated),
            _ => Strings.Update.Failure.Unknown(Code(failure.HResult)),
        };
    }

    /// <summary>How the release is NAMED in a sentence: its codename when the index knew one, else the semver, else
    /// the raw quad. Never empty — a toast that says "Wavee  is available" is worse than one that says a number.</summary>
    public static string ReleaseName(AppUpdateSnapshot snapshot)
    {
        if (snapshot is null) return "";
        if (snapshot.TargetCodename is { Length: > 0 } codename) return codename;
        if (snapshot.TargetSemVer is { Length: > 0 } semver) return semver;
        return snapshot.TargetQuad ?? "";
    }

    static string Code(int hresult)
        => hresult == 0 ? "0" : "0x" + hresult.ToString("X8", CultureInfo.InvariantCulture);
}
