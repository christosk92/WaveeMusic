using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;

namespace Wavee;

/// <summary>Zero-size, always-mounted chrome (the <c>PlaybackRuntimeChrome</c> precedent) that opens the one-time sidebar
/// design chooser exactly once per install, AFTER the shell's first painted frame. Renders nothing, ever.
///
/// The gate is exactly one boolean read — <c>SidebarDesignGating.ShouldShowChooser</c>, i.e.
/// <c>!settings.Get(WaveeSettings.SidebarOnboardingSeen)</c> — with no cross-referencing: <c>SidebarBootstrap</c> already
/// decided the marker at startup (an existing install has it set true and therefore never sees the chooser; a fresh
/// install has it false and is already defaulted to Curated).
///
/// Mounted inside <c>OverlayHost</c> by <c>WaveeShell</c>, which itself only mounts when authenticated — so the chooser can
/// never race the login takeover.</summary>
sealed class SidebarOnboardingChrome : Component
{
    readonly IAppSettings _settings;
    public SidebarOnboardingChrome(IAppSettings settings) => _settings = settings;

    public override Element Render()
    {
        var overlay = UseContext(Overlay.Service);
        var prefs = UseContext(SidebarPreferences.Slot);
        // The chooser's "Customize now" follow-up navigates to the customizer, so the chrome resolves the app-wide nav
        // action here. Context, not a ctor arg: this component remounts on a logout→login flip, and the shell's Go is
        // already published on NavCtx for exactly this kind of deep consumer.
        var go = UseContext(HistoryStore.NavCtx);
        var post = UsePost();
        var opened = UseRef(false);

        UseEffect(() =>
        {
            if (opened.Value || prefs is null) return;
            // Belt-and-braces over SidebarBootstrap's own ordering: the setup wizard's page 5 IS this same chooser
            // (Features/Setup/SetupChrome.cs), so while the wizard is still pending this chrome must never race it
            // open on its own — a factory-reset (or any path that re-arms SetupPending) must not resurrect a SECOND,
            // independent chooser popup behind/alongside the wizard's own page.
            if (SetupGating.IsPending(_settings)) return;
            if (!SidebarDesignGating.ShouldShowChooser(_settings)) return;
            opened.Value = true;
            // TWO nested posts: the first lands after this mount's commit, the second after the frame that PAINTED the
            // shell — so the user sees the app, then the chooser rises over it (never a dialog over a blank window).
            //
            // Because the shell remounts on logout→login, `opened` is a UseRef on a remountable component: the SETTINGS
            // MARKER (not the ref) is the durable guard, and the ref only prevents a double-open within one mount.
            post(() => post(() => OpenChooser(overlay, prefs, go)));
        }, DepKey.Empty);

        return new BoxEl { HitTestVisible = false, Shrink = 0f };
    }

    /// <summary>Opens the one-time chooser (§C6.2): a modal overlay with light-dismiss DISABLED carrying the shared
    /// three-card <c>SidebarDesignPicker</c>. Choosing a card calls <c>prefs.SwitchDesign</c> immediately (the pane
    /// behind the scrim changes as you click), and EVERY exit path — "Use this layout", "Not now", Escape, a
    /// shutdown-time close — burns <c>WaveeSettings.SidebarOnboardingSeen</c>, so no path leaves the marker false and the
    /// chooser can never appear twice.
    ///
    /// <para>The marker is written by the OVERLAY HANDLE's ClosedAction inside <see cref="SidebarDesignPicker.Open"/>,
    /// not here: hanging it on the one event every close path funnels through is what makes "no path forgets" a
    /// structural property rather than a review promise. This method therefore never writes the marker itself — and when
    /// <see cref="SidebarDesignPicker.Open"/> declines to open (no overlay/preference seam), the marker deliberately
    /// stays false, because burning it on a chooser that was never SHOWN would permanently deny the chooser to exactly
    /// the fresh installs it exists for.</para></summary>
    void OpenChooser(IOverlayService overlay, SidebarPreferences prefs, Action<string, string?> go)
        => _ = SidebarDesignPicker.Open(overlay, prefs, _settings, go);
}
