// ── Screens/Setup.UI.Runtime.cs ────────────────────────────────────────────────────────────────────────────────────────
// the runtime provisioning card's core host: the standalone dialog, and the signature summary sentence
//
// Role: CORE host (the picture is Classic's: `Themes\Classic\Structure\Views\Screens\ClassicSetup.Runtime.cs`, the `Setup.RuntimeCard` view, themes plan T11c)
// Owner: I
// Wave: 4
// Spec: ch 19 §9.4
//
// WHAT IS HERE. The dialog that hosts the card's two slots (the body and the one command row) at rung 2 of the modal width ladder. The card's state holder (`Setup.RuntimeModel`, one per
// open, shared by the body and the footer), the process-wide cells the banner gate and the other doors read (`Setup.Runtime`) and the host SEAM the SHELL half fills (`Setup.RuntimeHost`)
// are `RuntimeModel.cs` (C14, engine-free). Every decision the card draws is `Setup.RuntimeRules` in `Setup.cs` (CORE). (The nested signature sheet is plan §2's: the overlay file.)
//
// THE MOUNT POINTS. `Setup.OpenRuntimeCard(overlay, post)` is the standalone dialog the shell banner opens; the body and the footer read the active model from `Setup.Runtime.Active`. The
// wizard calls `Setup.Runtime.Begin(post, onClose, onWizardExit)` and its Local playback page draws the phases itself (`Setup.LocalPlayback`).

using FluentGpu.Controls;
using FluentGpu.Localization;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;

namespace Wavee;

public static partial class Setup
{
    static readonly SetupRuntimeCardProps s_cardBody = new()
    {
        Part = SetupDialogPart.Body, OpenSignature = OpenSignatureVerb, HasDiagnosticsPage = HasDiagnosticsPage,
    };

    static readonly SetupRuntimeCardProps s_cardFooter = new()
    {
        Part = SetupDialogPart.Footer, OpenSignature = OpenSignatureVerb, HasDiagnosticsPage = HasDiagnosticsPage,
    };

    static bool HasDiagnosticsPage() => Shell.PageFor(new Shell.Route(Shell.RouteKind.PlaybackDiagnostics)) is not null;

    /// <summary>The standalone setup dialog (the banner's "Set up", the other doors through
    /// <see cref="Runtime.OpenRequest"/>). ContentDialog at rung 2; its built-in buttons are cleared and the Footer owns
    /// the ONE command row, so a phase swap never re-opens the dialog; dismissal is vetoed while busy.</summary>
    public static OverlayHandle OpenRuntimeCard(IOverlayService overlay, Action<Action> post)
    {
        Log.Event(WaveeLogLevel.Info, "ui", "runtime.setup.open", "Playback runtime setup opened", null, -1, null,
            WaveeLogField.Of("issue", Runtime.Status.Peek().Issue.ToString()));
        var model = Runtime.Begin(post);
        var handle = ContentDialog.Show(overlay, d =>
        {
            d.Title = Loc.Get(Strings.Playback.Runtime.Title);
            d.DialogWidth = RuntimeRules.DialogWidth;
            d.PrimaryText = "";                  // PrimaryText defaults to "OK": clear all three or a phantom OK appears
            d.SecondaryText = "";
            d.CloseText = "";
            d.Content = Slots.View(ViewKeys.SetupRuntimeCard, s_cardBody);
            d.Footer = Slots.View(ViewKeys.SetupRuntimeCard, s_cardFooter);
            d.Closing = a => { if (model.IsBusy) a.Cancel = true; };
            d.Closed = _ => Runtime.End(model);
        });
        model.CloseHost = handle.Close;
        return handle;
    }

    /// <summary>The signature summary sentence (the ladder is <see cref="RuntimeRules.SignatureLine"/>; the wording is
    /// localized — 0.2.9 hard-coded it, ch 19 §6.10). Read by the runtime card, the signature sheet and Settings › Playback.</summary>
    public static string RuntimeSignatureSummary(RuntimeFacts facts)
    {
        string subject = facts.Signature?.Subject ?? "";
        return RuntimeRules.SignatureLine(facts) switch
        {
            RuntimeSignatureLine.SignedTrusted => Strings.Runtime.Sig.SignedBy(subject),
            RuntimeSignatureLine.SignedPinned => Strings.Runtime.Sig.SignedByPinned(subject),
            RuntimeSignatureLine.SignedOther => Strings.Runtime.Sig.SignedByTrust(subject,
                Loc.Get(RuntimeRules.TrustLocKey(facts.Signature?.Trust ?? RuntimeTrust.Unknown))),
            RuntimeSignatureLine.PinnedFingerprint => Loc.Get(Strings.Runtime.Sig.TrustedByFingerprint),
            RuntimeSignatureLine.VerifiedFingerprint => Loc.Get(Strings.Runtime.Sig.VerifiedFingerprint),
            RuntimeSignatureLine.NotTrustedOverride => Loc.Get(Strings.Runtime.Sig.NotTrustedOverride),
            RuntimeSignatureLine.CheckUnavailable => Loc.Get(Strings.Runtime.Sig.CheckUnavailable),
            _ => Loc.Get(Strings.Runtime.Sig.SignatureUnknown),
        };
    }
}
