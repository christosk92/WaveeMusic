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
}
