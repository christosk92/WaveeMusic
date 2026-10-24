// ── Themes/WinUi/Structure/Views/Screens/WinUiSetup.Pages.cs — Classic's wizard pages (themes plan 6.1, T11c) ────────────────
//
// From `Screens\Setup.UI.cs` (TermsPage, CrashConsentCard, LocalPlaybackPage, DownloadCard) and `Setup.UI.Theme.cs` (ThemePage). The Sign in page is `WinUiSetup.SignIn.cs`.
//
// The Theme step is the wizard's second page (M7a: it replaced the Layout page, whose two sidebar cards are now the first row of Classic's options): the Layout page's frame
// (the hero, the step count and the Back arrow are unchanged) and body vocabulary. A theme pick there is a theme switch under the plate: the picker and
// the options are the settings' builders the props carry.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Themes.Common;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.WinUi;

internal static partial class WinUiSetup
{
    // ── the Theme step ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The registered template of <see cref="ViewKeys.SetupTheme"/>: pick a theme and set its options. The options subscribe to the option cells and the sidebar, so a pick
    /// (or a change from the palette) shows at once.</summary>
    public static Element ThemeTemplate(SetupThemeProps props, in ViewContext ctx)
    {
        var kids = new List<Element>(3)
        {
            SetupText.Body(Loc.Get(Strings.Setup.Theme.Body)),
            props.ThemePicker(),
        };
        if (props.ThemeOptions() is { } options) kids.Add(options);
        return WizardFrame(Setup.WizardPage.Theme, Loc.Get(Strings.Setup.Theme.Title), SetupText.Stack(kids.ToArray()));
    }

    // ── page 0 · Terms ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The registered template of <see cref="ViewKeys.SetupTerms"/>.</summary>
    public static Element TermsTemplate(SetupTermsProps props, in ViewContext ctx)
    {
        var session = props.Session;
        var layout = WinUiSetupTiers.Of(ctx.Tier());
        var kids = new List<Element>(12)
        {
            SetupText.Lead(Loc.Get(Strings.Setup.Terms.Start)),
            SetupText.Body(Loc.Get(Strings.Setup.Terms.LastUpdated)),
            SetupText.Body(Loc.Get(Strings.Setup.Welcome.Lead)),
            SetupText.Lead(Loc.Get(Strings.Setup.Terms.Section1Title)), SetupText.Body(Loc.Get(Strings.Setup.Terms.Section1Body)),
            SetupText.Lead(Loc.Get(Strings.Setup.Terms.Section2Title)), SetupText.Body(Loc.Get(Strings.Setup.Terms.Section2Body)),
            SetupText.Lead(Loc.Get(Strings.Setup.Terms.Section3Title)), SetupText.Body(Loc.Get(Strings.Setup.Terms.Section3Body)),
            SetupText.Lead(Loc.Get(Strings.Setup.Terms.Section4Title)), SetupText.Body(Loc.Get(Strings.Setup.Terms.Section4Body)),
            SetupText.Group(
                SetupText.Secondary(Loc.Get(Strings.Setup.Terms.Fine)),
                CommonTouch.Link(Loc.Get(Strings.Setup.Terms.PrivacyLink), props.OpenPrivacy, layout.Target, ControlSize.Small)),
        };
        // crash-diagnostics plan §D/§F: the consent card never shows on a re-auth (WizardRules.ConsentShown gates it
        // the same way the durable write does in WizardModel.Primary, so the two never disagree).
        if (Setup.WizardRules.ConsentShown(session.Entry))
        {
            var consent = props.Consent;
            kids.Add(Embed.Comp(() => new CrashConsentCard(consent)) with { Key = "setup:terms:crash-consent" });
        }

        Element body = SetupText.Stack(kids.ToArray());
        return WizardFrame(Setup.WizardPage.Terms, Loc.Get(Setup.WizardRules.TermsHeaderKey(session.Entry)), body, backAutoPadding: false);
    }

    /// <summary>The Terms page's opt-in crash-reporting card (crash-diagnostics plan §D "Wizard consent"): title + an
    /// "Optional" pill, the one-line question, a radio choice (No · Ask me first · Always send, default Off; only the first two on a build that can't send) and the scrub
    /// summary (the page's fine print carries the Privacy link). The choice writes the setting the instant it changes — never waits for Continue, so a
    /// crash between the click and the wizard's own advance cannot lose it. Consent-asked itself is stamped once, on
    /// Continue, by <c>Setup.WizardRules.MarkConsentAsked</c> (<c>WizardModel.Primary</c>), not here.</summary>
    private sealed class CrashConsentCard(SetupConsent consent) : CommonTierComponent
    {
        public override Element Render()
        {
            var layout = WinUiSetupTiers.Of(ReadTier());
            // #165: a build that can't send (no ingest URL / key / quad) offers only No · Ask me first — "Always send"
            // would be a promise it cannot keep. The offered list is a prefix of Off/Ask/Auto, so the radio index stays
            // the stored Crash.Reporting value; the seed is the EFFECTIVE mode (a stored Auto reads Ask there).
            var index = UseSignal(consent.Seed());   // seeded once from the store
            var labels = new List<string>(3);
            for (int mode = 0; mode < consent.Choices; mode++)
                labels.Add(Loc.Get(mode switch
                {
                    0 => Strings.Setup.Terms.CrashNo,
                    1 => Strings.Setup.Terms.CrashAsk,
                    _ => Strings.Setup.Terms.CrashAlways,
                }));

            // A vertical radio group (the Windows privacy-settings shape), not a segmented switcher: three labels of
            // very different length never fit three equal-width segments, and a consent choice reads as a list.
            var set = consent.Set;
            Element choice = TouchRadios(labels, index, layout.Target, onChange: i => set(i));

            Element titleRow = new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Wrap = true,
                Children = [SetupText.Lead(Loc.Get(Strings.Setup.Terms.CrashTitle)), Pill(Loc.Get(Strings.Setup.Terms.CrashOptional))],
            };

            // The card carries the question as its header and the what-is-sent line as its description; the choice sits
            // in the content slot. The page already shows the Privacy link in the fine print above — not repeated here.
            return SetupText.Group(
                titleRow,
                SetupText.Card(Loc.Get(Strings.Setup.Terms.CrashLead), Loc.Get(Strings.Setup.Terms.CrashSub), null, choice));
        }
    }

    /// <summary>The "Optional" pill: MinHeight 22, 11.5/600, subtle fill (the release notes' hero / plate pill, in its non-accent form).</summary>
    private static Element Pill(string text) => new BoxEl
    {
        Shrink = 0f, MinHeight = WinUiSetupRules.PillMinHeight, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = WinUiSetupRules.PillPadding, Corners = CornerRadius4.All(Radii.Pill),
        Fill = Tok.FillSubtleSecondary,
        Children = [R.Type.MicroMeta(text) with { Weight = 600, MaxLines = 1, Color = Tok.TextSecondary }],
    };

    // ── page 2 · Local playback (owner I's runtime model and pieces, in the chapter's column) ──────────────────────

    /// <summary>The registered template of <see cref="ViewKeys.SetupLocalPlayback"/>.</summary>
    public static Element LocalPlaybackTemplate(SetupLocalPlaybackProps props, in ViewContext ctx)
    {
        var overlay = ctx.Hooks.UseContext(Overlay.Service);
        var layout = WinUiSetupTiers.Of(ctx.Tier());
        if (props.Session.Model is not { } m)   // no provisioning host (D3): the model-null arm — no Key, no motion, no fine print
            return WizardFrame(Setup.WizardPage.LocalPlayback, Loc.Get(Setup.Commands.RuntimeHeaderKey(null)), SetupText.Stack(
                SetupText.Lead(Loc.Get(Setup.Commands.RuntimeLeadKey(null))),
                SetupText.Body(Loc.Get(Strings.Playback.Runtime.NotActive))));

        RuntimePhase phase = m.Phase.Value;
        string arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
        var kids = new List<Element>(3) { SetupText.Lead(Loc.Get(Setup.Commands.RuntimeLeadKey(phase))), LocalContent(props, m, phase, arch, overlay, layout) };
        if (phase != RuntimePhase.Advanced)
            kids.Add(SetupText.Secondary(Loc.Get(Strings.Setup.LocalPlayback.FinePrint)) with { Key = "runtime:fineprint" });
        Element body = SetupText.Stack(kids.ToArray()) with
        {
            Key = "runtime:" + phase, Enter = BodyEnter, Exit = BodyExit, Transition = MotionTok.StandardEnter,
        };
        return WizardFrame(Setup.WizardPage.LocalPlayback, Loc.Get(Setup.Commands.RuntimeHeaderKey(phase)), body);
    }

    private static Element LocalContent(SetupLocalPlaybackProps props, Setup.RuntimeModel m, RuntimePhase phase, string arch, IOverlayService overlay, WinUiSetupLayout layout) => phase switch
    {
        RuntimePhase.Offer => SetupText.Group(OfferCard(arch), SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = Loc.Get(Strings.Setup.LocalPlayback.Advanced),
            HeaderIcon = Icons.Settings,
            Items =
            [
                SettingsExpander.Item(Loc.Get(Strings.Setup.LocalPlayback.AdvancedDll), Loc.Get(Strings.Setup.LocalPlayback.AdvancedDllSub),
                    isClickEnabled: true, onClick: m.PickFolder),
                SettingsExpander.Item(Loc.Get(Strings.Setup.LocalPlayback.AdvancedInstalled), Loc.Get(Strings.Setup.LocalPlayback.AdvancedInstalledSub),
                    isClickEnabled: true, onClick: m.UseInstalled),
                SettingsExpander.Item(Loc.Get(Strings.Setup.LocalPlayback.AdvancedVersion), Loc.Get(Strings.Setup.LocalPlayback.AdvancedVersionSub),
                    isClickEnabled: true, onClick: m.ShowAdvanced),
            ],
        })),
        RuntimePhase.FetchingCatalog => SetupText.Card(Loc.Get(Strings.Playback.Runtime.CheckingSupport),
            Loc.Get(Strings.Playback.Runtime.ReachingCatalog) + "  ·  " + arch, Icons.Download, ProgressBar.Indeterminate(WinUiSetupGeometry.ProgressWidth)),
        RuntimePhase.Downloading => Embed.Comp(() => new DownloadCard(m)),
        RuntimePhase.Verifying => SetupText.Card(Loc.Get(Strings.Playback.Runtime.Verifying),
            Loc.Get(Strings.Playback.Runtime.VerifyingCaption), Icons.Download, ProgressBar.Indeterminate(WinUiSetupGeometry.ProgressWidth)),
        RuntimePhase.Untrusted => SetupText.Group(
            InfoBar.Create(InfoBarSeverity.Warning, Loc.Get(Strings.Playback.Runtime.SignatureInvalid), Loc.Get(Strings.Playback.Runtime.UntrustedBody), isClosable: false),
            SetupText.Card(Loc.Get(Strings.Playback.Runtime.DetailVersion), m.ActiveEntry is { } e ? e.Version + "  ·  " + e.Arch : "—"),
            SetupText.Card(Loc.Get(Strings.Playback.Runtime.DetailSha256), m.ActiveEntry is { } h ? Setup.RuntimeRules.ShortHash(h.Sha256) : "—")),
        RuntimePhase.Ready => LocalReady(props, m, overlay, layout),
        RuntimePhase.Failed => SetupText.Group(
            InfoBar.Create(InfoBarSeverity.Error, Loc.Get(Strings.Playback.Runtime.Missing), m.Error.Value ?? Loc.Get(Strings.Playback.Runtime.NoPack), isClosable: false),
            OfferCard(arch)),
        _ => LocalAdvanced(m, layout),
    };

    private static Element OfferCard(string arch) =>
        SetupText.Card(Loc.Get(Strings.Setup.LocalPlayback.CardTitle), Strings.Setup.LocalPlayback.CardSub(arch), Icons.Download);

    private static Element LocalReady(SetupLocalPlaybackProps props, Setup.RuntimeModel m, IOverlayService overlay, WinUiSetupLayout layout)
    {
        Setup.RuntimeFacts facts = m.Status;
        string? location = facts.Location;
        var open = props.OpenSignature;
        var reveal = props.RevealFolder;
        var kids = new List<Element>(5);
        if (m.UpToDate.Value) kids.Add(SetupText.Body(Loc.Get(Strings.Playback.Runtime.UpToDate)));
        kids.Add(SetupText.Card(Loc.Get(Strings.Setup.LocalPlayback.Version), Setup.RuntimeRules.OrDash(facts.Version)));
        kids.Add(SetupText.Card(Loc.Get(Strings.Setup.LocalPlayback.Architecture), Setup.RuntimeRules.OrDash(facts.Arch)));
        kids.Add(SetupText.Card(Loc.Get(Strings.Setup.LocalPlayback.Signature), Setup.RuntimeSignatureSummary(facts),
            content: facts.Signature is null ? null
                : CommonTouch.Link(Loc.Get(Strings.Setup.LocalPlayback.View), () => open(overlay, facts), layout.Target)));
        kids.Add(SetupText.Card(Loc.Get(Strings.Setup.LocalPlayback.Location), Setup.RuntimeRules.OrDash(location),
            content: CommonTouch.Link(Loc.Get(Strings.Setup.LocalPlayback.OpenFolder), () => reveal(location), layout.Target)));
        return SetupText.Group(kids.ToArray());
    }

    /// <summary>The Advanced arm — owner I's catalog arms and pieces (<see cref="RuntimeVersionPicker"/>,
    /// <see cref="RuntimeLocalSourceRows"/>), mounted as the standalone card mounts them.</summary>
    private static Element LocalAdvanced(Setup.RuntimeModel m, WinUiSetupLayout layout)
    {
        var kids = new List<Element>(5) { RuntimeText(Loc.Get(Strings.Playback.Runtime.AdvancedBody)) };
        switch (Setup.RuntimeRules.AdvancedArm(m.Catalog.Value, m.SupportedPacks.Count))
        {
            case Setup.RuntimeAdvancedArm.Busy: kids.Add(RuntimeBusyRow(Loc.Get(Strings.Playback.Runtime.CheckingSupport))); break;
            case Setup.RuntimeAdvancedArm.Picker: kids.Add(RuntimeVersionPicker(m, null, layout)); break;
            case Setup.RuntimeAdvancedArm.NoPack: kids.Add(RuntimeText(Loc.Get(Strings.Playback.Runtime.NoPack))); break;
            case Setup.RuntimeAdvancedArm.Unreachable: kids.Add(RuntimeText(Loc.Get(Strings.Playback.Runtime.CatalogUnreachable))); break;
        }
        kids.AddRange(RuntimeLocalSourceRows(m, layout));
        return SetupText.Group(kids.ToArray());
    }

    /// <summary>The Downloading card on its own component, so a byte tick re-renders one card, never the page and its hero.</summary>
    private sealed class DownloadCard(Setup.RuntimeModel m) : Component
    {
        public override Element Render()
        {
            long received = m.Received.Value, total = m.Total.Value;
            return SetupText.Card(m.DownloadLabel.Value ?? Loc.Get(Strings.Playback.Runtime.Downloading),
                Setup.RuntimeRules.DownloadBytes(received, total), Icons.Download,
                total > 0 ? ProgressBar.Determinate(Setup.RuntimeRules.ProgressFraction(received, total), WinUiSetupGeometry.ProgressWidth)
                          : ProgressBar.Indeterminate(WinUiSetupGeometry.ProgressWidth));
        }
    }
}
