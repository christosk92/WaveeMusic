namespace Wavee;

/// <summary>Why this run of the setup wizard exists. Top-level (not nested in <c>SetupSession</c>, which references
/// <c>FluentGpu.Signals</c>) so the engine-free, test-included <c>SetupGating</c> can take it directly —
/// <see cref="SetupGating.CanDismiss"/>/<see cref="SetupGating.SkipsLocalPlayback"/> switch on it directly, and
/// <c>SetupGatingTests</c> drives the real decisions (source-included by <c>Wavee.Tests</c>, which has no
/// FluentGpu.Engine reference). <see cref="SetupGating.StepNumber"/>/<see cref="SetupGating.Progress"/> are keyed on
/// the page alone now — a <c>TermsRearm</c> run only ever visits <see cref="SetupPage.Terms"/>, so neither needs this
/// enum any more.
///
/// <para><c>FirstRun</c> — a fresh install walking Terms → Sign in → Local playback.</para>
/// <para><c>Reauth</c> — setup was already completed once but the account is signed out, so the wizard opens
/// straight on <see cref="SetupPage.SignIn"/>; it is Wavee's ONLY sign-in surface, and re-walking Terms for someone
/// who already accepted them would be nonsense.</para>
/// <para><c>TermsRearm</c> — a COMPLETED, still-signed-in install whose recorded terms acceptance is behind this
/// build's (<see cref="SetupGating.TermsVersion"/>/<see cref="SetupGating.NeedsTermsRearm"/>). The wizard re-opens
/// on Terms in its "updated terms" mode; Accept writes the new version and closes the wizard outright — there is
/// nothing else about a finished install left to ask. It is also the ONLY entry point Escape/light-dismiss may close
/// (<see cref="SetupGating.CanDismiss"/>): it is the one run with a live shell behind it.</para></summary>
public enum SetupEntryPoint { FirstRun, Reauth, TermsRearm }
