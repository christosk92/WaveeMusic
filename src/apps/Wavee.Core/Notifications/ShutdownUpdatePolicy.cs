namespace Wavee.Core;

/// <summary>
/// The "install a waiting update when I quit" decision, on its own so it can be unit-tested without a process to quit.
///
/// <para>The setting (<c>app.update.installOnQuit</c>, off by default) is the whole policy: updates normally apply on
/// the NEXT launch — Windows stages the package while Wavee is closed and the new version simply opens next time —
/// and turning this on means Wavee also spends the moments after you close it downloading and staging whatever the
/// feed already offered. Nothing restarts and nothing is scheduled: if the download does not finish, the update is
/// still waiting the next time the app runs.</para>
///
/// <para>Only <see cref="AppUpdateState.Available"/> and <see cref="AppUpdateState.Snoozed"/> qualify. A
/// <see cref="AppUpdateState.Failed"/> attempt must NOT be retried silently at quit (the user pressed something and
/// was told it failed; a quiet retry that fails again is invisible), and Downloading/Installing are already in flight.
/// </para>
/// </summary>
public static class ShutdownUpdatePolicy
{
    /// <summary>Should the orderly-shutdown path apply the pending update?</summary>
    /// <param name="installOnQuit">The persisted <c>app.update.installOnQuit</c> value.</param>
    /// <param name="state">The updater's last published state.</param>
    public static bool ShouldApply(bool installOnQuit, AppUpdateState state)
        => installOnQuit && state is AppUpdateState.Available or AppUpdateState.Snoozed;

    /// <summary>Did the quit-time apply reach a state worth reporting as finished? Anything else means the bounded
    /// wait timed out or the apply refused before it started (an unpackaged build opens the release page instead).</summary>
    public static bool IsSettled(AppUpdateState state)
        => state is AppUpdateState.Installing or AppUpdateState.Failed;
}
