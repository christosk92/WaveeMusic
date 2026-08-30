namespace Wavee;

/// <summary>What the sign-in page's STAGE column shows for a given <see cref="SetupSignInPhase"/>: the pairing pane
/// while a code is still live and worth showing (<see cref="Pairing"/> — Idle and Busy), or a single glyph badge +
/// caption once the flow has resolved to a terminal result (<see cref="Terminal"/> — Done/Failed/Expired/Premium),
/// where the pairing pane is unmounted outright. A top-level enum — not nested inside
/// <see cref="SetupSignInPresentation"/> — so its name doesn't collide with the
/// <see cref="SetupSignInPresentation.StageKind"/> function that computes it (a type and a method can't share a
/// name inside the same class).</summary>
public enum SignInStageKind : byte { Pairing, Terminal }

/// <summary>Pure presentation facts derived from <see cref="SetupSignInPhase"/> for the setup wizard's sign-in page
/// (page 2, Work package C). ENGINE-FREE BY CONSTRUCTION — no <c>FluentGpu.*</c>/<c>Loc</c>/<c>Signal&lt;T&gt;</c>
/// reference — exactly like <c>SetupStepState.cs</c>/<c>SetupGating.cs</c>: this file is source-included by
/// <c>Wavee.Tests</c> so a theory test drives the REAL projection, never a copy of it.
///
/// <para>The six phases split into three visual groups the page's Wide-tier stage/decision composition reads off
/// these functions rather than re-deriving the split inline: Idle (the pairing pane is fully live and interactive,
/// both option cards show), Busy (the pane fades to a 22% reminder — still mounted so the cross-fade has something
/// to fade FROM, but no longer interactive — while the approve card takes over the decision column), and the four
/// terminal facets (Done/Failed/Expired/Premium — the pane is gone; a single glyph badge stands in for it).</para></summary>
static class SetupSignInPresentation
{
    /// <summary>The compact QR/pairing pane's opacity: fully live at Idle, a faint 22% reminder while Busy (the
    /// code can no longer usefully be approved from — the flow has already moved on to finalizing the account it
    /// already has — but disappearing it outright would read as the code just stopped working rather than as
    /// progress), invisible once the flow has resolved to any terminal facet.</summary>
    public static float PaneOpacity(SetupSignInPhase phase) => phase switch
    {
        SetupSignInPhase.Idle => 1f,
        SetupSignInPhase.Busy => 0.22f,
        _ => 0f,
    };

    /// <summary>Whether the pane should still accept focus/hit-testing — ONLY at Idle. At Busy the pane is faded to
    /// 22% but still technically mounted (the cross-fade needs it there to fade FROM); Tab must not be able to
    /// reach a barely-visible link, and a click on it would race a flow that has already moved past pairing.</summary>
    public static bool PaneInteractive(SetupSignInPhase phase) => phase == SetupSignInPhase.Idle;

    /// <summary>Whether the decision column shows the two option cards (browser / scan) — Idle only. Busy has
    /// already committed to a path (the approve card takes over); every terminal facet has a result to show
    /// instead of a choice.</summary>
    public static bool ShowsOptionCards(SetupSignInPhase phase) => phase == SetupSignInPhase.Idle;

    /// <summary>Whether the decision column shows an approve/progress card — Idle (a PENDING preview of the four
    /// finalizing steps, so the user knows what happens right after they approve) and Busy (the SAME four steps,
    /// now live off the real bridge signal).</summary>
    public static bool ShowsApproveCard(SetupSignInPhase phase) =>
        phase is SetupSignInPhase.Idle or SetupSignInPhase.Busy;

    /// <summary>Whether the decision column leads with the Spotify brand row — every phase except Done, whose
    /// column instead leads straight with the resolved identity card (a brand row above an identity card the user

    /// <summary>Which stage composition the page's Wide-tier stage column mounts for this phase.</summary>
    public static SignInStageKind StageKind(SetupSignInPhase phase) =>
        phase is SetupSignInPhase.Idle or SetupSignInPhase.Busy ? SignInStageKind.Pairing : SignInStageKind.Terminal;
}
