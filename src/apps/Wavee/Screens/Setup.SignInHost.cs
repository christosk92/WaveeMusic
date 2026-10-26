// ── Screens/Setup.SignInHost.cs ────────────────────────────────────────────────────────────────────────────────────────
// the sign-in flows as a view sees them (`ISetupSignInHost`, themes plan T11c)
//
// Role: CORE (engine-free: reads the sign-in state, the session and the account; no element, token or role)
//
// The door's body and footer and the wizard's Sign in page used to read `Spotify.SignIn.State`, `Spotify.Status`, `Spotify.Fault` and the account's user row themselves. A theme may not name
// those types (WVTH002), so the views read these facts instead: every fact method reads the same signals the components read, in the same order, so the reading render subscribes to
// exactly what it did before. The verbs are the ones the door and the wizard already shared (`WizardModel.cs` §4).

using Wavee.Theming.Contracts;

namespace Wavee;

public static partial class Setup
{
    /// <summary>The one implementation of <see cref="ISetupSignInHost"/> (stateless: everything it answers is read live).</summary>
    sealed class SignInHost : ISetupSignInHost
    {
        public static readonly SignInHost Instance = new();

        public SetupSignInFacts Facts()
        {
            Spotify.SignInState st = Spotify.SignIn.State.Value;
            Spotify.SessionPhase phase = Spotify.Status.Value;
            Spotify.SessionFault fault = Spotify.Fault.Value;
            SignInFacet facet = SignInRules.Project(in st, phase, fault);
            return new SetupSignInFacts(
                facet,
                st.Handed,
                phase == Spotify.SessionPhase.Minting,
                Commands.BusyMessageKey(in st),
                facet == SignInFacet.Failed ? SignInRules.FailureKey(in st, fault) : "",
                facet != SignInFacet.Done && SignInRules.NeedsPairingCode(in st),
                st.HasChallenge ? st.UserCode : "");
        }

        public SetupPairingFacts Pairing()
        {
            Spotify.SignInState st = Spotify.SignIn.State.Value;
            bool working = st.Code == Spotify.SignInStage.Starting || (st.Code == Spotify.SignInStage.Idle && !st.Handed);
            if (!st.HasChallenge) return new SetupPairingFacts(false, working, st.Code == Spotify.SignInStage.Failed, "", "", 0L);
            string scanUri = st.VerificationUriComplete.Length > 0 ? st.VerificationUriComplete : st.VerificationUri;
            return new SetupPairingFacts(true, working, st.Code == Spotify.SignInStage.Failed, st.UserCode, scanUri, st.CodeExpiresAtMs);
        }

        public SetupLoginStepFacts Step()
        {
            Spotify.SignInState st = Spotify.SignIn.State.Value;
            Spotify.SessionPhase phase = Spotify.Status.Value;
            return new SetupLoginStepFacts(Commands.LoginStep(st.Handed, phase), phase == Spotify.SessionPhase.Failed);
        }

        public bool NeedsPairingChallenge(WizardPage activePage)
        {
            Spotify.SignInState st = Spotify.SignIn.State.Value;
            Spotify.SessionFault fault = Spotify.Fault.Value;
            SignInFacet facet = SignInRules.Project(in st, Spotify.Status.Value, fault);
            return Commands.NeedsPairingChallenge(activePage, facet, in st);
        }

        public SetupAccountFacts Account()
        {
            _ = Entities.Current.Users.Changed.Value;   // subscribe: the account's identity lands a tick after Online
            User me = User.Me;
            bool known = me.IsValid && me.Knows(UserFields.Identity);
            Spotify.Session s = Spotify.Current;
            string account = s.Username.IsEmpty ? "" : Entities.Strings.Resolve(s.Username);
            string name = SignInPresentation.DisplayNameFor(known ? new AccountName(account, Entities.Strings.Resolve(me.NameId)) : null, null) ?? account;
            return new SetupAccountFacts(name, known ? Controls.ArtUrl(me.ImageId) : null, s.Tier == Spotify.Tier.Premium);
        }

        public void StartBrowser() => Setup.StartBrowser();
        public void StartPairing() => Setup.StartPairing();
        public void CancelBrowser() => Setup.CancelBrowser();
        public void Retry() => RetryFor(Spotify.SignIn.State.Peek())();
        public void SwitchAccount() => Setup.SwitchAccount();
        public void CloseDoor() => CloseSignIn();
        public void OpenSignUp() => OpenExternal(SpotifySignUpUrl);
        public void OpenPremium() => OpenExternal(SpotifyPremiumUrl);
        public void OpenExternal(string uri) => Setup.OpenExternal(uri);
    }
}
