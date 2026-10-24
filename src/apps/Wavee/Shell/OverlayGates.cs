// ── Shell/OverlayGates.cs — the decisions the overlay lane makes before it shows a banner or a toast ───────────────
//
// Role: CORE (themes plan §5.1, PR C8; extraction X22). `Shell.Overlays.UI.cs` used to read the facts (settings, the
// setup model's signals, the playback fault) AND decide inside its watcher components. The decisions are here, pure; the
// watchers read the facts and ask. Which facts are read, and when, stays the watchers' business (a read inside an effect is
// a subscription), so this file takes plain values.

namespace Wavee;

/// <summary>Whether the playback-runtime banner and the update / runtime-missing toasts may appear.</summary>
public static class OverlayGates
{
    /// <summary>The floating playback-runtime banner: something to say, not dismissed, and neither the first-run wizard's
    /// own Local-playback page (<paramref name="wizardCovering"/>) nor a still-pending wizard
    /// (<paramref name="setupPending"/>) is asking the same question already.</summary>
    public static bool ShowsRuntimeBanner(Setup.RuntimeIssue issue, bool dismissed, bool wizardCovering, bool setupPending)
        => Setup.RuntimeRules.ShowsBanner(issue, dismissed, wizardCovering, setupPending);

    /// <summary>The runtime-missing toast (the third door to the setup card): a track failed because the local runtime is
    /// missing AND a host could fix it. Suppressed while the wizard is asking the same question, and while the banner is
    /// already on screen saying it (evaluated as it would be with no wizard in the way).</summary>
    public static bool ShowsRuntimeMissingToast(Playback.Fault fault, bool hostInstalled, bool wizardCovering, bool setupPending,
        Setup.RuntimeIssue issue, bool dismissed)
    {
        if (fault != Playback.Fault.RuntimeMissing || !hostInstalled) return false;
        if (wizardCovering || setupPending) return false;
        return !Setup.RuntimeRules.ShowsBanner(issue, dismissed, wizardCovering: false, setupPending: false);
    }

    /// <summary>A planned update toast is raised unless the user silenced the App-updates dial outright.</summary>
    public static bool ShowsUpdateToast(NotifyLevel appUpdatesLevel) => appUpdatesLevel != NotifyLevel.Off;
}
