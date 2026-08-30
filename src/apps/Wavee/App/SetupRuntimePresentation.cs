using System;

namespace Wavee;

/// <summary>Pure presentation math for the setup wizard's live runtime transfer. Kept outside the component so byte
/// overshoot and catalog hash formatting stay deterministic and headlessly testable.
///
/// <para>Work package D (local playback, page 3) additions: which numbered step
/// (<see cref="SetupStepState"/> — Download/Verify/Ready) each <see cref="PlaybackRuntimeSetupModel.Phase"/>
/// highlights in <c>SetupDecision.StepCard</c>'s ladder, which of the decision column's escape-hatch chip rows a
/// phase shows, and which lower panel kind (<see cref="SetupRuntimeStagePanel"/>) the stage column shows underneath
/// the hero art. Engine-free by construction — like <c>SetupGating.cs</c> and <c>SetupStepState.cs</c> — so
/// <c>SetupRuntimePresentationTests</c> can drive every real <see cref="PlaybackRuntimeSetupModel.Phase"/> value
/// headlessly.</para></summary>
static class SetupRuntimePresentation
{
    public static float ProgressFraction(long received, long total)
        => total <= 0 ? 0f : Math.Clamp((float)((double)received / total), 0f, 1f);

    public static string ShortHash(string hash) => hash.Length > 8
        ? string.Concat(hash.AsSpan(0, 4), "…", hash.AsSpan(hash.Length - 4, 4))
        : hash;

    /// <summary>Which of the three canonical steps (Download/Verify/Ready) each phase highlights in the decision
    /// column's step-card ladder. Total over every <see cref="PlaybackRuntimeSetupModel.Phase"/> value — including
    /// Failed/Advanced, whose ladder is never actually SHOWN (<see cref="ShowsStepCards"/> is false for both) —
    /// so a phase added later can't silently fall through an unhandled arm.</summary>
    public static (SetupStepState Download, SetupStepState Verify, SetupStepState Ready) StepStates(
        PlaybackRuntimeSetupModel.Phase phase) => phase switch
    {
        PlaybackRuntimeSetupModel.Phase.Offer =>
            (SetupStepState.Pending, SetupStepState.Pending, SetupStepState.Pending),
        PlaybackRuntimeSetupModel.Phase.FetchingCatalog or PlaybackRuntimeSetupModel.Phase.Downloading =>
            (SetupStepState.Current, SetupStepState.Pending, SetupStepState.Pending),
        PlaybackRuntimeSetupModel.Phase.Verifying =>
            (SetupStepState.Done, SetupStepState.Current, SetupStepState.Pending),
        PlaybackRuntimeSetupModel.Phase.Untrusted =>
            (SetupStepState.Done, SetupStepState.Attention, SetupStepState.Pending),
        PlaybackRuntimeSetupModel.Phase.Ready =>
            (SetupStepState.Done, SetupStepState.Done, SetupStepState.Done),
        // Never rendered (ShowsStepCards is false) — Download reads Failed so a caller that DOES render this ladder
        // by mistake still shows something honest rather than a silently-stale Pending.
        PlaybackRuntimeSetupModel.Phase.Failed =>
            (SetupStepState.Failed, SetupStepState.Pending, SetupStepState.Pending),
        PlaybackRuntimeSetupModel.Phase.Advanced =>
            (SetupStepState.Pending, SetupStepState.Pending, SetupStepState.Pending),
        _ => (SetupStepState.Pending, SetupStepState.Pending, SetupStepState.Pending),
    };

    /// <summary>The decision column shows the numbered step ladder for every phase EXCEPT Failed (its own status
    /// block already explains what went wrong — a step ladder under an error reads as noise) and Advanced (a
    /// detour to a version picker, not a step in the ladder).</summary>
    public static bool ShowsStepCards(PlaybackRuntimeSetupModel.Phase phase) =>
        phase is not (PlaybackRuntimeSetupModel.Phase.Failed or PlaybackRuntimeSetupModel.Phase.Advanced);

    /// <summary>Offer and Failed both show the "Choose a folder / Use installed Spotify / Choose a version"
    /// escape-hatch chip row — Offer because it's the first thing a network-shy user reaches for, Failed because
    /// the network path just proved itself unreliable.</summary>
    public static bool ShowsAdvancedChips(PlaybackRuntimeSetupModel.Phase phase) =>
        phase is PlaybackRuntimeSetupModel.Phase.Offer or PlaybackRuntimeSetupModel.Phase.Failed;

    /// <summary>Advanced's own chip row drops "Choose a version" from the trio above — the version list IS what's
    /// already on screen — down to just the two offline-source chips.</summary>
    public static bool ShowsLocalSourceChips(PlaybackRuntimeSetupModel.Phase phase) =>
        phase == PlaybackRuntimeSetupModel.Phase.Advanced;

    /// <summary>Which lower panel the stage column shows under the hero art for a given phase. Used both to pick the
    /// panel's content AND as its <c>Key</c> suffix, so two phases sharing a panel KIND (Verifying/Untrusted both
    /// showing a verify-flavored fact box) cross-fade their content instead of remounting.</summary>
    public static SetupRuntimeStagePanel StagePanelFor(PlaybackRuntimeSetupModel.Phase phase) => phase switch
    {
        PlaybackRuntimeSetupModel.Phase.FetchingCatalog or PlaybackRuntimeSetupModel.Phase.Downloading =>
            SetupRuntimeStagePanel.Progress,
        PlaybackRuntimeSetupModel.Phase.Verifying or PlaybackRuntimeSetupModel.Phase.Untrusted =>
            SetupRuntimeStagePanel.Verify,
        PlaybackRuntimeSetupModel.Phase.Ready => SetupRuntimeStagePanel.Ready,
        _ => SetupRuntimeStagePanel.Facts, // Offer, Failed, Advanced — the static "what gets installed" facts card.
    };
}

/// <summary>The stage column's lower panel kind for the local-playback page (<see cref="SetupRuntimePresentation.StagePanelFor"/>):
/// the static install facts, a live download/verify progress readout, the verify fact box (shared by Verifying and
/// Untrusted), or the Ready badge/detail/links group.</summary>
public enum SetupRuntimeStagePanel { Facts, Progress, Verify, Ready }
