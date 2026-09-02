using System;

namespace Wavee;

/// <summary>Pure presentation math for the setup wizard's live runtime transfer. Kept outside the component so byte
/// overshoot and catalog hash formatting stay deterministic and headlessly testable.
///
/// <para>Engine-free by construction — like <c>SetupGating.cs</c> — so <c>SetupRuntimePresentationTests</c> can
/// drive every real <see cref="PlaybackRuntimeSetupModel.Phase"/> value headlessly. The old stage/decision panel
/// routing (<c>ShowsAdvancedChips</c>/<c>ShowsLocalSourceChips</c>/<c>StagePanelFor</c>/<c>SetupRuntimeStagePanel</c>)
/// is gone with the tier ladder — the Local playback page now composes its per-phase <c>SettingsCard</c>s directly
/// (<c>SetupLocalPlaybackPage</c>), the same way <c>PlaybackRuntimeSetupCard.SetupBody</c> already does for the
/// standalone dialog.</para></summary>
static class SetupRuntimePresentation
{
    public static float ProgressFraction(long received, long total)
        => total <= 0 ? 0f : Math.Clamp((float)((double)received / total), 0f, 1f);

    public static string ShortHash(string hash) => hash.Length > 8
        ? string.Concat(hash.AsSpan(0, 4), "…", hash.AsSpan(hash.Length - 4, 4))
        : hash;
}
