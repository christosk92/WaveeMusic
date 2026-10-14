// ── Theming/Contracts/SetupProps.cs — the props of the setup screens (themes plan 6.0, T11c) ───────────────────────────────────────
//
// The wizard's model (`Setup.WizardModel`), the runtime model (`Setup.RuntimeModel`) and the sign-in flows are core and outlive every mount; the props hand a view the
// model it reads (its signals subscribe the reading slot, exactly as the components that used to read them did) and the verbs and facts it may not reach itself.
// A theme may not name `Spotify`, `Shell`, `Entities` or `Prefs` (WVTH002): the sign-in state arrives as the small fact records below, read LIVE through
// `ISetupSignInHost`, and the few shell / settings builders the pages place arrive as `Func<Element>`.

using FluentGpu.Controls;
using FluentGpu.Dsl;

namespace Wavee.Theming.Contracts;

/// <summary>Which of the two slots of a dialog a <see cref="SetupSignInDoorProps"/> / <see cref="SetupRuntimeCardProps"/> mounts.</summary>
public enum SetupDialogPart : byte
{
    /// <summary>The dialog's content.</summary>
    Body,
    /// <summary>The dialog's command row.</summary>
    Footer,
}

/// <summary>The props of <see cref="ViewKeys.SetupWizard"/>. One instance per plate (the core host builds it once).</summary>
public sealed record SetupWizardProps
{
    /// <summary>The live session: page, direction, the footer row, the step number, the progress and the Primary / Secondary / Back verbs.</summary>
    public required Setup.WizardModel Session { get; init; }

    /// <summary>The Terms page's props.</summary>
    public required SetupTermsProps Terms { get; init; }

    /// <summary>The Sign in page's props.</summary>
    public required SetupSignInPageProps SignIn { get; init; }

    /// <summary>The Theme page's props (the wizard's second page, on the frame of <c>WizardPage.Theme</c>).</summary>
    public required SetupThemeProps Theme { get; init; }

    /// <summary>The Local playback page's props.</summary>
    public required SetupLocalPlaybackProps LocalPlayback { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.SetupTerms"/>.</summary>
public sealed record SetupTermsProps
{
    /// <summary>The live session (the entry decides the header and whether the consent card shows).</summary>
    public required Setup.WizardModel Session { get; init; }

    /// <summary>The privacy link's verb.</summary>
    public required Action OpenPrivacy { get; init; }

    /// <summary>The crash-reporting consent card's source.</summary>
    public required SetupConsent Consent { get; init; }
}

/// <summary>The crash-reporting consent card's facts and verb. The offered choices are a prefix of Off / Ask / Auto, so a choice index IS the stored mode.</summary>
/// <param name="Choices">How many choices this build offers (3, or 2 on a build that cannot send: "Always send" would be a promise it cannot keep).</param>
/// <param name="Seed">The index the card opens on (read once at mount: the stored mode, EFFECTIVE for a build that cannot send).</param>
/// <param name="Set">Writes the choice the instant it changes (never waits for Continue).</param>
public sealed record SetupConsent(int Choices, Func<int> Seed, Action<int> Set);

/// <summary>The props of <see cref="ViewKeys.SetupSignIn"/>.</summary>
public sealed record SetupSignInPageProps
{
    /// <summary>The live session (the page mints the pairing code only while it is the ACTIVE page).</summary>
    public required Setup.WizardModel Session { get; init; }

    /// <summary>The sign-in flows' facts and verbs.</summary>
    public required ISetupSignInHost Host { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.SetupTheme"/>.</summary>
public sealed record SetupThemeProps
{
    /// <summary>The theme picker cards (core: the settings builder).</summary>
    public required Func<Element> ThemePicker { get; init; }

    /// <summary>The active theme's options (null: the theme has none). Called from inside the page's render, so the page subscribes to the option cells.</summary>
    public required Func<Element?> ThemeOptions { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.SetupLocalPlayback"/>.</summary>
public sealed record SetupLocalPlaybackProps
{
    /// <summary>The live session (its runtime model is begun when the wizard first reaches this page; null while no host is installed).</summary>
    public required Setup.WizardModel Session { get; init; }

    /// <summary>The nested digital-signature sheet.</summary>
    public required Action<IOverlayService, Setup.RuntimeFacts> OpenSignature { get; init; }

    /// <summary>Reveals a folder in the file manager.</summary>
    public required Action<string?> RevealFolder { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.SetupSignInDoor"/>.</summary>
public sealed record SetupSignInDoorProps
{
    /// <summary>The slot this mount is.</summary>
    public required SetupDialogPart Part { get; init; }

    /// <summary>The sign-in flows' facts and verbs.</summary>
    public required ISetupSignInHost Host { get; init; }
}

/// <summary>The props of <see cref="ViewKeys.SetupRuntimeCard"/>.</summary>
public sealed record SetupRuntimeCardProps
{
    /// <summary>The slot this mount is.</summary>
    public required SetupDialogPart Part { get; init; }

    /// <summary>The nested digital-signature sheet.</summary>
    public required Action<IOverlayService, Setup.RuntimeFacts> OpenSignature { get; init; }

    /// <summary>Whether the playback diagnostics page exists in this build (the footer's "View diagnostics" link).</summary>
    public required Func<bool> HasDiagnosticsPage { get; init; }
}

/// <summary>The sign-in surface's facts: the facet and the few state bits the cards read. A projection of the core sign-in state and the session; reading it inside a
/// render subscribes the reader to them.</summary>
/// <param name="Facet">The surface's facet (<c>Setup.SignInRules.Project</c>).</param>
/// <param name="Handed">A token was handed over (the ladder walks the session's phase; before that the browser's wait is the whole story).</param>
/// <param name="Minting">The session is minting its profile (the door's Busy step line).</param>
/// <param name="BusyMessageKey">The Busy message's loc key (<c>Setup.Commands.BusyMessageKey</c>).</param>
/// <param name="FailureKey">The Failed facet's sentence as a loc key ("" for every other facet).</param>
/// <param name="NeedsPairingCode">The surface wants a pairing code minted (none live or asked for, none lapsed, no token handed over, and not Done).</param>
/// <param name="UserCode">The live pairing code ("" while none).</param>
public readonly record struct SetupSignInFacts(Setup.SignInFacet Facet, bool Handed, bool Minting, string BusyMessageKey, string FailureKey,
                                               bool NeedsPairingCode, string UserCode);

/// <summary>The pairing card's facts (reads the sign-in state only, so a phase change never re-renders the card).</summary>
/// <param name="HasChallenge">A pairing code is live.</param>
/// <param name="Working">No code yet and one is being asked for.</param>
/// <param name="CodeFailed">The last code request failed (otherwise a missing code has lapsed).</param>
/// <param name="UserCode">The code ("" while none).</param>
/// <param name="ScanUri">What the QR encodes: the complete verification uri, else the plain one.</param>
/// <param name="ExpiresAtMs">When the code lapses (unix ms).</param>
public readonly record struct SetupPairingFacts(bool HasChallenge, bool Working, bool CodeFailed, string UserCode, string ScanUri, long ExpiresAtMs);

/// <summary>The Busy ladder's facts: the current rung and whether the login failed on it.</summary>
public readonly record struct SetupLoginStepFacts(int Step, bool Failed);

/// <summary>"Is this you?": the signed-in account as the confirmation row shows it.</summary>
/// <param name="Name">The display name (the account id stands in until the identity lands).</param>
/// <param name="ImageUrl">The avatar's art url (null until the identity is known).</param>
/// <param name="Premium">The account is Premium (otherwise the row says Free).</param>
public readonly record struct SetupAccountFacts(string Name, string? ImageUrl, bool Premium);

/// <summary>The sign-in flows, as a view sees them: live facts and the verbs. One stable object (the core implements it); every fact method subscribes the calling
/// render to what it read, exactly as the components that read the sign-in state did.</summary>
public interface ISetupSignInHost
{
    /// <summary>The facet and the state bits the cards read (subscribes to the sign-in state, the session phase and the fault).</summary>
    SetupSignInFacts Facts();

    /// <summary>The pairing card's facts (subscribes to the sign-in state only).</summary>
    SetupPairingFacts Pairing();

    /// <summary>The Busy ladder's facts (subscribes to the sign-in state and the session phase).</summary>
    SetupLoginStepFacts Step();

    /// <summary>The wizard's Sign in page mints its pairing code while it is the ACTIVE page and no code is live, asked for, lapsed or failed
    /// (<c>Setup.Commands.NeedsPairingChallenge</c>; subscribes to the sign-in state, the session phase and the fault).</summary>
    bool NeedsPairingChallenge(Setup.WizardPage activePage);

    /// <summary>The signed-in account (subscribes to the user table and the session).</summary>
    SetupAccountFacts Account();

    /// <summary>Starts the browser flow.</summary>
    void StartBrowser();

    /// <summary>Starts the pairing-code flow.</summary>
    void StartPairing();

    /// <summary>Cancels the browser flow.</summary>
    void CancelBrowser();

    /// <summary>"Try again": retries the flow that failed (the pairing code when only it failed, the browser otherwise).</summary>
    void Retry();

    /// <summary>"Not me": signs this PC out.</summary>
    void SwitchAccount();

    /// <summary>Closes the door's dialog.</summary>
    void CloseDoor();

    /// <summary>Opens Spotify's sign-up page.</summary>
    void OpenSignUp();

    /// <summary>Opens Spotify's Premium page.</summary>
    void OpenPremium();

    /// <summary>Opens the pairing page (or any uri) in the browser.</summary>
    void OpenExternal(string uri);
}
