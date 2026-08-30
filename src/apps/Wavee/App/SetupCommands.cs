using System;
using Wavee.Core;

namespace Wavee;

/// <summary>The setup wizard's SignIn page, folded from the ten <see cref="LoginPhase"/> values the login takeover
/// (<c>LoginView.cs</c>) already switches over. Six facets, not ten — several phases render the same footer.</summary>
public enum SetupSignInPhase { Idle, Busy, Done, Failed, Expired, Premium }

/// <summary>The setup wizard's LocalPlayback page — the local-playback-runtime provisioning states
/// (<c>PlaybackRuntimeSetupCard.cs</c>'s flow), reduced to the facets this page's footer needs to distinguish.</summary>
public enum SetupRuntimeFacet { Offer, Catalog, Versions, Downloading, Verifying, Untrusted, Ready, Failed }

/// <summary>The setup wizard's Done page — whether the wizard's own "apply everything now" step (persisting the
/// choices made on the earlier pages) has run yet.</summary>
public enum SetupApplyState { Idle, Running, Done }

/// <summary>The primary button's visual treatment. <c>Spotify</c> is the SignIn page's branded green button (matching
/// the login takeover); <c>Standard</c> exists for completeness with the button-kind vocabulary used elsewhere in the
/// app even though no row in the table below currently needs it for a PRIMARY button.</summary>
public enum SetupButtonKind { Accent, Spotify, Standard }

/// <summary>Everything <see cref="SetupCommands.Resolve"/> needs to answer "what does the footer look like right now":
/// which page, and — for the three pages whose footer depends on more than the page alone — the relevant sub-state.
/// <paramref name="SignInSkipped"/>/<paramref name="IsRerun"/> do not change THIS page's buttons (see
/// <see cref="SetupCommands.Resolve"/>'s remarks); they exist so the wizard shell's page-flow decisions
/// (<see cref="SetupGating.NextPage"/>/<see cref="SetupGating.PrevPage"/>) and this resolver share one context type.</summary>
public readonly record struct SetupCtx(SetupPage Page, SetupSignInPhase SignIn, SetupRuntimeFacet Runtime,
                                       SetupApplyState Apply, bool SignInSkipped, bool IsRerun);

/// <summary>The footer's whole answer for one <see cref="SetupCtx"/>: which buttons, what they say (loc KEYS, never
/// literal text), whether they're enabled, and whether Back may show. <c>null</c> means "no such button" — a disabled
/// button is instead a non-null key with its <c>*Enabled</c> flag false, so a caller can always tell "not offered"
/// apart from "offered but not right now".</summary>
public readonly record struct SetupCommandRow(string? PrimaryKey, string? SecondaryKey, SetupButtonKind PrimaryKind,
                                              bool PrimaryEnabled, bool SecondaryEnabled, bool BlocksDismiss, bool ShowBack);

/// <summary>The wizard's whole label table in one reviewable, unit-testable place (§ setup wizard). Engine-free by
/// construction (System + Wavee.Core + the generated <c>Strings</c> consts only) — the buttons themselves are the
/// wizard shell's job; this file only decides which loc KEYS they carry, whether they're enabled, and whether a
/// long-running step should swallow the dismiss/Back affordances. <see cref="SidebarDesignGating"/> is the sibling
/// precedent for splitting a label/gating decision out into its own pure, test-included file.</summary>
static class SetupCommands
{
    /// <summary>The SignIn page owns minting its pairing challenge. Requesting and terminal phases return false,
    /// so reactive re-renders cannot start a second request.</summary>
    public static bool NeedsPairingChallenge(SetupPage activePage, LoginPhase phase, bool hasChallenge)
        => activePage == SetupPage.SignIn && !hasChallenge
           && phase is LoginPhase.LoggedOut or LoginPhase.AwaitingApproval;

    /// <summary>Folds the ten <see cref="LoginPhase"/> values (the login takeover's own switch, <c>LoginView.cs</c>)
    /// into the six <see cref="SetupSignInPhase"/> facets this page's footer needs. <paramref name="step"/> and
    /// <paramref name="auth"/> are accepted for symmetry with the bridge's full login snapshot (a future facet split —
    /// e.g. a slower Finalizing step warranting its own footer copy — would read them) but do not affect today's fold:
    /// every "still working" phase (Finalizing/RequestingCode/AwaitingBrowser/SilentResume) already reads as one Busy
    /// footer. <see cref="LoginPhase.LoggedOut"/> — before anything has been asked for — folds to Idle alongside
    /// <see cref="LoginPhase.AwaitingApproval"/> (the SignIn page's own two-pane QR/browser screen, which carries its
    /// own always-live "Log in" affordance rather than a busy state).</summary>
    public static SetupSignInPhase Project(LoginPhase phase, LoginStep step, AuthStatus auth) => phase switch
    {
        LoginPhase.Finalizing or LoginPhase.RequestingCode or LoginPhase.AwaitingBrowser or LoginPhase.SilentResume
            => SetupSignInPhase.Busy,
        LoginPhase.ChallengeExpired => SetupSignInPhase.Expired,
        LoginPhase.Failed => SetupSignInPhase.Failed,
        LoginPhase.PremiumRequired => SetupSignInPhase.Premium,
        LoginPhase.Authenticated => SetupSignInPhase.Done,
        // LoggedOut, AwaitingApproval
        _ => SetupSignInPhase.Idle,
    };

    /// <summary>The footer for <paramref name="ctx"/>. <see cref="SetupCtx.SignInSkipped"/> and
    /// <see cref="SetupCtx.IsRerun"/> are deliberately NOT read here: they decide which PAGES the wizard visits
    /// (<see cref="SetupGating.NextPage"/>/<see cref="SetupGating.PrevPage"/> skip SignIn entirely when
    /// already-authenticated), not what any one page's buttons say once it IS visited. A caller must never ask this
    /// resolver for <see cref="SetupPage.SignIn"/> while skipping it — the page-flow functions above make sure it never
    /// has to.</summary>
    public static SetupCommandRow Resolve(in SetupCtx ctx)
    {
        SetupCommandRow row = ctx.Page switch
        {
            SetupPage.Welcome => new SetupCommandRow(Strings.Setup.StartSetup, Strings.Setup.NotNow, SetupButtonKind.Accent, true, true, false, false),
            SetupPage.Terms => new SetupCommandRow(Strings.Setup.Accept, Strings.Setup.Decline, SetupButtonKind.Accent, true, true, false, false),
            SetupPage.SignIn => SignInRow(ctx.SignIn),
            SetupPage.LocalPlayback => LocalPlaybackRow(ctx.Runtime),
            SetupPage.Appearance => new SetupCommandRow(Strings.Setup.Continue, Strings.Setup.DecideForMe, SetupButtonKind.Accent, true, true, false, false),
            SetupPage.Sidebar => new SetupCommandRow(Strings.Setup.UseThisLayout, Strings.Setup.DecideForMe, SetupButtonKind.Accent, true, true, false, false),
            SetupPage.Sound => new SetupCommandRow(Strings.Setup.Continue, Strings.Setup.DecideForMe, SetupButtonKind.Accent, true, true, false, false),
            SetupPage.Notifications => new SetupCommandRow(Strings.Setup.Continue, Strings.Setup.DecideForMe, SetupButtonKind.Accent, true, true, false, false),
            SetupPage.Done => DoneRow(ctx.Apply),
            _ => throw new ArgumentOutOfRangeException(nameof(ctx), ctx.Page, "Unknown SetupPage."),
        };

        // The prototype starts Back on SignIn. Terms already carries the explicit Accept/Decline decision and must
        // not also reserve a third way out; long-running middle states suppress Back as before.
        bool showBack = ctx.Page is >= SetupPage.SignIn and <= SetupPage.Notifications && !row.BlocksDismiss;
        return row with { ShowBack = showBack };
    }

    static SetupCommandRow SignInRow(SetupSignInPhase phase) => phase switch
    {
        SetupSignInPhase.Idle => new SetupCommandRow(Strings.Auth.LogIn, Strings.Auth.Close, SetupButtonKind.Spotify, true, true, false, false),
        SetupSignInPhase.Busy => new SetupCommandRow(Strings.Auth.SigningIn, Strings.Auth.Cancel, SetupButtonKind.Spotify, false, true, false, false),
        SetupSignInPhase.Done => new SetupCommandRow(Strings.Setup.SignIn.YesContinue, Strings.Setup.SignIn.NotMe, SetupButtonKind.Accent, true, true, false, false),
        SetupSignInPhase.Failed => new SetupCommandRow(Strings.Auth.TryAgain, Strings.Auth.Close, SetupButtonKind.Spotify, true, true, false, false),
        SetupSignInPhase.Expired => new SetupCommandRow(Strings.Auth.GetNewCode, Strings.Auth.Close, SetupButtonKind.Spotify, true, true, false, false),
        SetupSignInPhase.Premium => new SetupCommandRow(Strings.Auth.Upgrade, Strings.Auth.UseAnotherAccount, SetupButtonKind.Accent, true, true, false, false),
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "Unknown SetupSignInPhase."),
    };

    static SetupCommandRow LocalPlaybackRow(SetupRuntimeFacet facet) => facet switch
    {
        SetupRuntimeFacet.Offer => new SetupCommandRow(Strings.Playback.Runtime.DownloadSetup, Strings.Playback.Runtime.NotNow, SetupButtonKind.Accent, true, true, false, false),
        // Checking support / downloading / verifying are all short, unattended, non-cancel-via-Back network steps:
        // Back is suppressed (via BlocksDismiss) so the user can't rewind mid-request, but an explicit Cancel remains
        // (Verifying is the one exception below — a signature check is fast enough that even Cancel is not offered).
        SetupRuntimeFacet.Catalog => new SetupCommandRow(Strings.Playback.Runtime.CheckingSupport, Strings.Auth.Cancel, SetupButtonKind.Accent, false, true, true, false),
        SetupRuntimeFacet.Versions => new SetupCommandRow(Strings.Playback.Runtime.Install, Strings.Playback.Runtime.Back, SetupButtonKind.Accent, true, true, false, false),
        SetupRuntimeFacet.Downloading => new SetupCommandRow(Strings.Playback.Runtime.Downloading, Strings.Auth.Cancel, SetupButtonKind.Accent, false, true, true, false),
        SetupRuntimeFacet.Verifying => new SetupCommandRow(Strings.Playback.Runtime.Verifying, null, SetupButtonKind.Accent, false, false, true, false),
        SetupRuntimeFacet.Untrusted => new SetupCommandRow(Strings.Playback.Runtime.LoadAnyway, Strings.Playback.Runtime.Back, SetupButtonKind.Accent, true, true, false, false),
        SetupRuntimeFacet.Ready => new SetupCommandRow(Strings.Setup.Continue, null, SetupButtonKind.Accent, true, false, false, false),
        SetupRuntimeFacet.Failed => new SetupCommandRow(Strings.Playback.Runtime.TryAgain, Strings.Playback.Runtime.NotNow, SetupButtonKind.Accent, true, true, false, false),
        _ => throw new ArgumentOutOfRangeException(nameof(facet), facet, "Unknown SetupRuntimeFacet."),
    };

    static SetupCommandRow DoneRow(SetupApplyState apply) => apply switch
    {
        SetupApplyState.Idle => new SetupCommandRow(Strings.Setup.Continue, Strings.Setup.NotNow, SetupButtonKind.Accent, true, true, false, false),
        // Present-but-disabled (not absent): the button already reads what it will do once Apply finishes, it just
        // cannot be clicked while the wizard is still writing settings out.
        SetupApplyState.Running => new SetupCommandRow(Strings.Setup.OpenWavee, null, SetupButtonKind.Accent, false, false, true, false),
        SetupApplyState.Done => new SetupCommandRow(Strings.Setup.OpenWavee, null, SetupButtonKind.Accent, true, false, false, false),
        _ => throw new ArgumentOutOfRangeException(nameof(apply), apply, "Unknown SetupApplyState."),
    };
}
