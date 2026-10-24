// ── Theming/Contracts/ViewKeys.Setup.cs — the setup screens' published view keys (themes plan 6.0, T11c) ──────────────────────────
//
// T11c publishes the first-run wizard and the sign-in / runtime-provisioning surfaces: the plate with its footer (`Setup.Wizard`), its four pages (Terms, Sign in, the
// Theme step, Local playback) and the two dialog bodies the shell opens without the wizard (the sign-in door, the playback runtime card).
//
// What stays core: the wizard's model (`Setup.WizardModel`: page, direction, Primary / Secondary / Back, the step count, the progress), the boot decision and the open
// funnel, the overlay handle, the sign-in flows and their facts, the runtime provisioning model and its verbs, the Lottie hero loader, the crash-consent write, the QR
// encoder. A theme draws what the props and the models carry.

using FluentGpu.Theming;

namespace Wavee.Theming.Contracts;

public static partial class ViewKeys
{
    /// <summary>The wizard plate: the card, its keyboard chords, the keep-alive page stack, the footer and the Back button. Mounted by the core plate host
    /// (the overlay's content). Props: <see cref="SetupWizardProps"/>.</summary>
    public static readonly ViewKey<SetupWizardProps> SetupWizard = new("Setup.Wizard");

    /// <summary>The wizard's Terms page (the agreement and the crash-reporting consent card). Props: <see cref="SetupTermsProps"/>.</summary>
    public static readonly ViewKey<SetupTermsProps> SetupTerms = new("Setup.Terms");

    /// <summary>The wizard's Sign in page (the browser card, the pairing card, the Busy ladder, "Is this you?"). Props: <see cref="SetupSignInPageProps"/>.</summary>
    public static readonly ViewKey<SetupSignInPageProps> SetupSignIn = new("Setup.SignIn");

    /// <summary>The wizard's Theme step: the picker and the active theme's options. Props: <see cref="SetupThemeProps"/>.</summary>
    public static readonly ViewKey<SetupThemeProps> SetupTheme = new("Setup.Theme");

    /// <summary>The wizard's Local playback page (the runtime provisioning phases on the plate). Props: <see cref="SetupLocalPlaybackProps"/>.</summary>
    public static readonly ViewKey<SetupLocalPlaybackProps> SetupLocalPlayback = new("Setup.LocalPlayback");

    /// <summary>The sign-in door's dialog: its body and its footer (two slots of one ContentDialog). Props: <see cref="SetupSignInDoorProps"/>.</summary>
    public static readonly ViewKey<SetupSignInDoorProps> SetupSignInDoor = new("Setup.SignInDoor");

    /// <summary>The standalone playback runtime card: its body and its command row (two slots of one ContentDialog). Props: <see cref="SetupRuntimeCardProps"/>.</summary>
    public static readonly ViewKey<SetupRuntimeCardProps> SetupRuntimeCard = new("Setup.RuntimeCard");
}
