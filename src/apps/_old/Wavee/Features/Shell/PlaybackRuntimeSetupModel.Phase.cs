namespace Wavee;

/// <summary>The <see cref="PlaybackRuntimeSetupModel.Phase"/> enum, split into its own engine-free file (a partial-class
/// split, NOT a new type — <c>PlaybackRuntimeSetupModel.Phase</c> resolves to the exact same fully-qualified name every
/// existing call site already uses) so <c>App/SetupRuntimePresentation.cs</c>'s pure per-phase presentation math can
/// depend on it, and <c>Wavee.Tests/SetupRuntimePresentationTests.cs</c> can enumerate every value, WITHOUT dragging
/// <c>PlaybackRuntimeSetupCard.cs</c>'s FluentGpu.Controls/Dsl/Hooks/WindowsApi.Dialogs dependencies (and the
/// Wavee.Backend.Audio/Wavee.SpotifyLive.Audio.Runtime graph behind them) into the test project.</summary>
public sealed partial class PlaybackRuntimeSetupModel
{
    public enum Phase { Offer, FetchingCatalog, Downloading, Verifying, Untrusted, Ready, Failed, Advanced }

    /// <summary>Whether the standalone "Local playback is ready" toast (<c>Succeed</c>, this class's own file) should
    /// fire on this success. False when the model is wizard-hosted (<paramref name="wizardHosted"/> —
    /// <see cref="OnWizardExit"/> is set): the setup wizard's own LocalPlayback page already shows the Ready state in
    /// place, so a toast on top of it would repeat the same news the user is already looking at (the same
    /// "don't double the ask" rationale as <c>SetupGating.SuppressesRuntimePrompts</c>). True everywhere else — the
    /// Settings/banner-launched standalone dialog has no in-place Ready readout of its own to lean on.</summary>
    public static bool ShowsReadyToast(bool wizardHosted) => !wizardHosted;
}
