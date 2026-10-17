// ── Themes/Zune/Structure/Views/Screens/ZuneSetup.Pages.cs — Zune's wizard pages: Terms, Theme and Local playback (themes plan 6.3, P5 Z8b) ──────────
//
// The Sign in page is `ZuneSetup.SignIn.cs`. The Theme step is the wizard's second page: the picker and the active theme's options are the settings' keys (`Settings.ThemePicker`,
// `Settings.ThemeOptions`), which the props hand over as builders; a theme pick there is a theme switch under the plate. The pages draw the title and body of their frame
// (`WizardFrame`); the accent column and the footer are the plate's.

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Themes.Common;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;
using Z = Wavee.Themes.Zune.ZuneSetupRules;

namespace Wavee.Themes.Zune;

internal static partial class ZuneSetup
{
    /// <summary>The kit of the window tier a dialog or page is drawn in (its commands and discs).</summary>
    static ZuneKit KitOf(ZuneSetupLayout layout) => new(ZuneSettingsTiers.For(layout.Tier));

    // ── the Theme step ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The registered template of <see cref="ViewKeys.SetupTheme"/>: pick a theme and set its options. The options subscribe to the option cells, so a pick (or a change
    /// from the palette) shows at once.</summary>
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
        var layout = ZuneSetupTiers.Of(ctx.Tier());
        var k = KitOf(layout);
        var kids = new List<Element>(12)
        {
            SetupText.Lead(Loc.Get(Strings.Setup.Terms.Start)),
            SetupText.Secondary(Loc.Get(Strings.Setup.Terms.LastUpdated)),
            SetupText.Body(Loc.Get(Strings.Setup.Welcome.Lead)),
            SetupText.Lead(Loc.Get(Strings.Setup.Terms.Section1Title)), SetupText.Body(Loc.Get(Strings.Setup.Terms.Section1Body)),
            SetupText.Lead(Loc.Get(Strings.Setup.Terms.Section2Title)), SetupText.Body(Loc.Get(Strings.Setup.Terms.Section2Body)),
            SetupText.Lead(Loc.Get(Strings.Setup.Terms.Section3Title)), SetupText.Body(Loc.Get(Strings.Setup.Terms.Section3Body)),
            SetupText.Lead(Loc.Get(Strings.Setup.Terms.Section4Title)), SetupText.Body(Loc.Get(Strings.Setup.Terms.Section4Body)),
            SetupText.Group(
                SetupText.Secondary(Loc.Get(Strings.Setup.Terms.Fine)),
                new BoxEl { Direction = 0, AlignSelf = FlexAlign.Start, Children = [k.Cmd(Loc.Get(Strings.Setup.Terms.PrivacyLink), props.OpenPrivacy)] }),
        };
        // The consent card never shows on a re-auth (`WizardRules.ConsentShown` gates it the same way the durable write does in `WizardModel.Primary`, so the two never disagree).
        if (Setup.WizardRules.ConsentShown(session.Entry))
        {
            var consent = props.Consent;
            kids.Add(Embed.Comp(() => new CrashConsentCard(consent)) with { Key = "zsetup:terms:crash-consent" });
        }
        return WizardFrame(Setup.WizardPage.Terms, Loc.Get(Setup.WizardRules.TermsHeaderKey(session.Entry)), SetupText.Stack(kids.ToArray()));
    }

    /// <summary>The Terms page's opt-in crash-reporting card: the title with an "optional" word, the one-line question, a radio choice (no, ask me first, always send; default off;
    /// only the first two on a build that cannot send) and the scrub summary. The choice writes the setting the instant it changes (never waits for Continue), so a crash between
    /// the click and the wizard's own advance cannot lose it. Consent-asked itself is stamped once, on Continue, by `Setup.WizardRules.MarkConsentAsked`.</summary>
    private sealed class CrashConsentCard(SetupConsent consent) : CommonTierComponent
    {
        public override Element Render()
        {
            var layout = ZuneSetupTiers.Of(ReadTier());
            // A build that can't send (no ingest URL / key / quad) offers only no / ask me first: "always send" would be a promise it cannot keep. The offered list is a prefix
            // of off / ask / auto, so the radio index stays the stored mode; the seed is the EFFECTIVE mode (a stored auto reads ask there).
            var index = UseSignal(consent.Seed());   // seeded once from the store
            var labels = new List<string>(3);
            for (int mode = 0; mode < consent.Choices; mode++)
                labels.Add(Loc.Get(mode switch
                {
                    0 => Strings.Setup.Terms.CrashNo,
                    1 => Strings.Setup.Terms.CrashAsk,
                    _ => Strings.Setup.Terms.CrashAlways,
                }));
            var set = consent.Set;
            return SetupText.Group(
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Wrap = true,
                    Children = [ZuneText.Group(Loc.Get(Strings.Setup.Terms.CrashTitle)), ZuneText.Hint(Loc.Get(Strings.Setup.Terms.CrashOptional))],
                },
                SetupText.Card(Loc.Get(Strings.Setup.Terms.CrashLead), Loc.Get(Strings.Setup.Terms.CrashSub), null, TouchRadios(labels, index, layout.Target, onChange: i => set(i))));
        }
    }

    // ── page 3 · Local playback ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The registered template of <see cref="ViewKeys.SetupLocalPlayback"/>.</summary>
    public static Element LocalPlaybackTemplate(SetupLocalPlaybackProps props, in ViewContext ctx)
    {
        var overlay = ctx.Hooks.UseContext(Overlay.Service);
        var layout = ZuneSetupTiers.Of(ctx.Tier());
        if (props.Session.Model is not { } m)   // no provisioning host: the model-null arm, no Key, no motion, no fine print
            return WizardFrame(Setup.WizardPage.LocalPlayback, Loc.Get(Setup.Commands.RuntimeHeaderKey(null)), SetupText.Stack(
                SetupText.Lead(Loc.Get(Setup.Commands.RuntimeLeadKey(null))),
                SetupText.Body(Loc.Get(Strings.Playback.Runtime.NotActive))));

        RuntimePhase phase = m.Phase.Value;
        string arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
        var kids = new List<Element>(3) { SetupText.Lead(Loc.Get(Setup.Commands.RuntimeLeadKey(phase))), LocalContent(props, m, phase, arch, overlay, layout) };
        if (phase != RuntimePhase.Advanced)
            kids.Add(SetupText.Secondary(Loc.Get(Strings.Setup.LocalPlayback.FinePrint)) with { Key = "zsetup:runtime:fineprint" });
        Element body = SetupText.Stack(kids.ToArray()) with
        {
            Key = "zsetup:runtime:" + phase, Enter = BodyEnter, Exit = BodyExit, Transition = MotionTok.StandardEnter,
        };
        return WizardFrame(Setup.WizardPage.LocalPlayback, Loc.Get(Setup.Commands.RuntimeHeaderKey(phase)), body);
    }

    private static Element LocalContent(SetupLocalPlaybackProps props, Setup.RuntimeModel m, RuntimePhase phase, string arch, IOverlayService overlay, ZuneSetupLayout layout) => phase switch
    {
        RuntimePhase.Offer => SetupText.Group(OfferCard(arch),
            SetupText.Card(Loc.Get(Strings.Setup.LocalPlayback.AdvancedDll), Loc.Get(Strings.Setup.LocalPlayback.AdvancedDllSub), Icons.Folder, null, m.PickFolder),
            SetupText.Card(Loc.Get(Strings.Setup.LocalPlayback.AdvancedInstalled), Loc.Get(Strings.Setup.LocalPlayback.AdvancedInstalledSub), Icons.MusicNote, null, m.UseInstalled),
            SetupText.Card(Loc.Get(Strings.Setup.LocalPlayback.AdvancedVersion), Loc.Get(Strings.Setup.LocalPlayback.AdvancedVersionSub), Icons.Settings, null, m.ShowAdvanced)),
        RuntimePhase.FetchingCatalog => SetupText.Card(Loc.Get(Strings.Playback.Runtime.CheckingSupport),
            Loc.Get(Strings.Playback.Runtime.ReachingCatalog) + "  ·  " + arch, Icons.Download, ProgressBar.Indeterminate(Z.ProgressWidth)),
        RuntimePhase.Downloading => Embed.Comp(() => new DownloadCard(m)),
        RuntimePhase.Verifying => SetupText.Card(Loc.Get(Strings.Playback.Runtime.Verifying),
            Loc.Get(Strings.Playback.Runtime.VerifyingCaption), Icons.Download, ProgressBar.Indeterminate(Z.ProgressWidth)),
        RuntimePhase.Untrusted => SetupText.Group(
            ZuneKit.Notice(InfoBarSeverity.Warning, Loc.Get(Strings.Playback.Runtime.SignatureInvalid), Loc.Get(Strings.Playback.Runtime.UntrustedBody)),
            SetupText.Card(Loc.Get(Strings.Playback.Runtime.DetailVersion), m.ActiveEntry is { } e ? e.Version + "  ·  " + e.Arch : Z.Dash),
            SetupText.Card(Loc.Get(Strings.Playback.Runtime.DetailSha256), m.ActiveEntry is { } h ? Setup.RuntimeRules.ShortHash(h.Sha256) : Z.Dash)),
        RuntimePhase.Ready => LocalReady(props, m, overlay, layout),
        RuntimePhase.Failed => SetupText.Group(
            ZuneKit.Notice(InfoBarSeverity.Error, Loc.Get(Strings.Playback.Runtime.Missing), m.Error.Value ?? Loc.Get(Strings.Playback.Runtime.NoPack)),
            OfferCard(arch)),
        _ => LocalAdvanced(m, layout),
    };

    private static Element OfferCard(string arch) =>
        SetupText.Card(Loc.Get(Strings.Setup.LocalPlayback.CardTitle), Strings.Setup.LocalPlayback.CardSub(arch), Icons.Download);

    private static Element LocalReady(SetupLocalPlaybackProps props, Setup.RuntimeModel m, IOverlayService overlay, ZuneSetupLayout layout)
    {
        var k = KitOf(layout);
        Setup.RuntimeFacts facts = m.Status;
        string? location = facts.Location;
        var open = props.OpenSignature;
        var reveal = props.RevealFolder;
        var kids = new List<Element>(5);
        if (m.UpToDate.Value) kids.Add(SetupText.Body(Loc.Get(Strings.Playback.Runtime.UpToDate)));
        kids.Add(SetupText.Card(Loc.Get(Strings.Setup.LocalPlayback.Version), Setup.RuntimeRules.OrDash(facts.Version)));
        kids.Add(SetupText.Card(Loc.Get(Strings.Setup.LocalPlayback.Architecture), Setup.RuntimeRules.OrDash(facts.Arch)));
        kids.Add(SetupText.Card(Loc.Get(Strings.Setup.LocalPlayback.Signature), Setup.RuntimeSignatureSummary(facts),
            content: facts.Signature is null ? null : k.Cmd(Loc.Get(Strings.Setup.LocalPlayback.View), () => open(overlay, facts))));
        kids.Add(SetupText.Card(Loc.Get(Strings.Setup.LocalPlayback.Location), Setup.RuntimeRules.OrDash(location),
            content: k.Cmd(Loc.Get(Strings.Setup.LocalPlayback.OpenFolder), () => reveal(location))));
        return SetupText.Group(kids.ToArray());
    }

    /// <summary>The Advanced arm: the catalog arms and pieces, mounted as the standalone card mounts them.</summary>
    private static Element LocalAdvanced(Setup.RuntimeModel m, ZuneSetupLayout layout)
    {
        var kids = new List<Element>(5) { RuntimeText(Loc.Get(Strings.Playback.Runtime.AdvancedBody)) };
        switch (Setup.RuntimeRules.AdvancedArm(m.Catalog.Value, m.SupportedPacks.Count))
        {
            case Setup.RuntimeAdvancedArm.Busy: kids.Add(RuntimeBusyRow(Loc.Get(Strings.Playback.Runtime.CheckingSupport))); break;
            case Setup.RuntimeAdvancedArm.Picker: kids.Add(RuntimeVersionPicker(m, null, layout)); break;
            case Setup.RuntimeAdvancedArm.NoPack: kids.Add(RuntimeText(Loc.Get(Strings.Playback.Runtime.NoPack))); break;
            case Setup.RuntimeAdvancedArm.Unreachable: kids.Add(RuntimeText(Loc.Get(Strings.Playback.Runtime.CatalogUnreachable))); break;
        }
        kids.AddRange(RuntimeLocalSourceRows(m));
        return SetupText.Group(kids.ToArray());
    }

    /// <summary>The Downloading card on its own component, so a byte tick re-renders one card, never the page.</summary>
    private sealed class DownloadCard(Setup.RuntimeModel m) : Component
    {
        public override Element Render()
        {
            long received = m.Received.Value, total = m.Total.Value;
            return SetupText.Card(m.DownloadLabel.Value ?? Loc.Get(Strings.Playback.Runtime.Downloading),
                Setup.RuntimeRules.DownloadBytes(received, total), Icons.Download,
                total > 0 ? ProgressBar.Determinate(Setup.RuntimeRules.ProgressFraction(received, total), Z.ProgressWidth) : ProgressBar.Indeterminate(Z.ProgressWidth));
        }
    }
}
