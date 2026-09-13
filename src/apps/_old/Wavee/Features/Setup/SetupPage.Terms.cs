using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Localization;

namespace Wavee;

/// <summary>Page 0 · Terms — Rise's own <c>TermsPage</c>: the full licence agreement printed inline and left to
/// scroll, no summary card / disclosure / checkbox. Replaces the old <c>SetupPage.Welcome.cs</c> (a "Zune" kicker +
/// headline bookend with a click-to-expand terms card folded in) + <c>SetupTermsCard.cs</c> (the disclosure itself) —
/// the wizard's first page IS the terms page now, and it is the first thing a cold install shows — the dialog opens
/// immediately (<see cref="SetupPreAuthOpener"/>), with no pre-dialog splash in front of it.
///
/// <para><see cref="SetupEntryPoint.TermsRearm"/> mode: a COMPLETED, still-signed-in install whose recorded terms
/// acceptance fell behind this build's (<see cref="SetupGating.NeedsTermsRearm"/>) lands HERE instead of Local
/// playback or Settings — only the header swaps to "We've updated the terms"; the body is identical (it is the same
/// agreement). Accept writes the new <c>TermsAcceptedVersion</c> and closes the wizard outright
/// (<see cref="SetupSession.Primary"/>) instead of advancing to Sign in, because a re-armed, already-signed-in
/// install has nothing else left to ask.</para></summary>
sealed class SetupTermsPage : Component
{
    public override Element Render()
    {
        bool rearm = SetupSession.Current?.Entry == SetupEntryPoint.TermsRearm;
        string header = Loc.Get(rearm ? Strings.Setup.Terms.UpdatedTitle : Strings.Setup.Terms.Header);

        var kids = new System.Collections.Generic.List<Element>(14)
        {
            SetupText.Lead(Loc.Get(Strings.Setup.Terms.Start)),
            SetupText.Body(Loc.Get(Strings.Setup.Terms.LastUpdated)),
            SetupText.Body(Loc.Get(Strings.Setup.Welcome.Lead)),
        };
        foreach (var (sectionTitle, sectionBody) in Sections())
        {
            kids.Add(SetupText.Lead(sectionTitle));
            kids.Add(SetupText.Body(sectionBody));
        }
        kids.Add(SetupText.Group(
            SetupText.Secondary(Loc.Get(Strings.Setup.Terms.Fine)),
            HyperlinkButton.Create(Loc.Get(Strings.Setup.Terms.PrivacyLink), OpenPrivacy, size: ControlSize.Small)));

        Element body = SetupText.Stack([.. kids]);
        return SetupPageHost.Frame(SetupPage.Terms, header, body, backAutoPadding: false);
    }

    /// <summary>Wavee's own privacy statement — a link to the repository's <c>PRIVACY.md</c> rather than a fifth
    /// agreement section: it describes what the APP does with local data, a different document from the terms being
    /// accepted here, and it must stay readable/greppable outside the running app.</summary>
    const string PrivacyUrl = "https://github.com/christosk92/WaveeMusic/blob/main/PRIVACY.md";
    static void OpenPrivacy() => LoginView.OpenUrl(PrivacyUrl);

    /// <summary>The four agreement sections for the CURRENT locale. Deliberately a method, not a cached static array:
    /// a locale switch re-renders the wizard, and a frozen array would keep serving the old language forever.</summary>
    static (string Title, string Body)[] Sections() =>
    [
        (Loc.Get(Strings.Setup.Terms.Section1Title), Loc.Get(Strings.Setup.Terms.Section1Body)),
        (Loc.Get(Strings.Setup.Terms.Section2Title), Loc.Get(Strings.Setup.Terms.Section2Body)),
        (Loc.Get(Strings.Setup.Terms.Section3Title), Loc.Get(Strings.Setup.Terms.Section3Body)),
        (Loc.Get(Strings.Setup.Terms.Section4Title), Loc.Get(Strings.Setup.Terms.Section4Body)),
    ];
}
