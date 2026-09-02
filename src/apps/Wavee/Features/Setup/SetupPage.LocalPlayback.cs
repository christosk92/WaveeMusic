using System.Collections.Generic;
using System.Runtime.InteropServices;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;

namespace Wavee;

/// <summary>Page 2 · Local playback — the wizard's LAST page (<see cref="SetupGating.IsLastPage"/>: there is no Done
/// page to advance into; Ready's primary finishes the wizard outright). REUSES <see cref="PlaybackRuntimeSetupModel"/>
/// and <c>PlaybackRuntimeSetupCard.SetupBody</c>'s internal statics wholesale — <see cref="SetupPagePlaceholders"/>'s
/// capture wrapper has already constructed the model (<see cref="SetupSession.EnsureRuntime"/>) by the time this page
/// mounts, so the phase this page shows is the exact same <see cref="PlaybackRuntimeSetupModel.PhaseSig"/> the footer
/// reads.
///
/// <para>One content column, Rise-styled: a download <c>SettingsCard</c> + a <c>SettingsExpander</c> "Advanced" while
/// Offer; a progress <c>SettingsCard</c> (label/byte or percent text + a 162-wide <c>ProgressBar</c> in its
/// <c>Content</c> slot) for the three network phases; a warning <c>InfoBar</c> + two fact cards for Untrusted; four
/// detail cards + fine print for Ready; an error <c>InfoBar</c> + the Offer card again for Failed; the model's own
/// <c>SetupBody.VersionPicker</c> for Advanced. No account card here (the previous page already confirmed it), no
/// facts table, no chips, no stage column.</para></summary>
sealed class SetupLocalPlaybackPage : Component
{
    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var bridge = UseContext(PlaybackBridge.Slot);
        var overlay = UseContext(Overlay.Service);
        var post = UsePost();

        var session = SetupSession.Current;
        var model = session?.Runtime;
        if (model is null && session is not null && svc?.Settings is { } settings0 && bridge is not null)
            model = session.EnsureRuntime(svc, settings0, bridge, post);

        if (model is null)
        {
            Element emptyBody = SetupText.Stack(
                SetupText.Lead(Loc.Get(Strings.Setup.LocalPlayback.Lead)),
                SetupText.Body(Loc.Get(Strings.Playback.Runtime.NotActive)));
            return SetupPageHost.Frame(SetupPage.LocalPlayback, Loc.Get(Strings.Setup.LocalPlayback.Header), emptyBody);
        }

        var helper = new SetupBody(model);
        var phase = model.PhaseSig.Value;
        string header = Loc.Get(HeaderFor(phase));

        var kids = new List<Element>(6) { SetupText.Lead(Loc.Get(LeadFor(phase))) };
        kids.Add(PhaseContent(phase, model, helper, overlay));
        if (phase != PlaybackRuntimeSetupModel.Phase.Advanced)
            kids.Add(SetupText.Secondary(Loc.Get(Strings.Setup.LocalPlayback.FinePrint)) with { Key = "runtime:fineprint" });

        Element body = SetupText.Stack([.. kids]) with
        {
            Key = "runtime:" + phase,
            Enter = new EnterExit(Dy: 6f, Opacity: 0f, Active: true),
            Exit = new EnterExit(Dy: -4f, Opacity: 0f, Active: true),
            Transition = MotionTok.StandardEnter,
        };

        return SetupPageHost.Frame(SetupPage.LocalPlayback, header, body);
    }

    static string HeaderFor(PlaybackRuntimeSetupModel.Phase phase) => phase switch
    {
        PlaybackRuntimeSetupModel.Phase.Untrusted => Strings.Playback.Runtime.SignatureInvalid,
        PlaybackRuntimeSetupModel.Phase.Ready => Strings.Playback.Runtime.Ready,
        PlaybackRuntimeSetupModel.Phase.Advanced => Strings.Playback.Runtime.ChooseVersion,
        _ => Strings.Setup.LocalPlayback.Header,
    };

    static string LeadFor(PlaybackRuntimeSetupModel.Phase phase) =>
        phase == PlaybackRuntimeSetupModel.Phase.Ready ? Strings.Setup.LocalPlayback.ReadyLead : Strings.Setup.LocalPlayback.Lead;

    static Element PhaseContent(PlaybackRuntimeSetupModel.Phase phase, PlaybackRuntimeSetupModel model, SetupBody helper, IOverlayService overlay) => phase switch
    {
        PlaybackRuntimeSetupModel.Phase.Offer => OfferGroup(model),
        PlaybackRuntimeSetupModel.Phase.FetchingCatalog => CatalogCard(),
        PlaybackRuntimeSetupModel.Phase.Downloading => DownloadingCard(model),
        PlaybackRuntimeSetupModel.Phase.Verifying => VerifyingCard(),
        PlaybackRuntimeSetupModel.Phase.Untrusted => UntrustedGroup(model),
        PlaybackRuntimeSetupModel.Phase.Ready => ReadyGroup(model, helper, overlay),
        PlaybackRuntimeSetupModel.Phase.Failed => FailedGroup(model),
        PlaybackRuntimeSetupModel.Phase.Advanced => helper.Advanced(),
        _ => new BoxEl(),
    };

    // ── Offer: the download card + an Advanced disclosure (SettingsExpander) ───────────────────────────────────────
    static Element OfferGroup(PlaybackRuntimeSetupModel model) => SetupText.Group(DownloadCard(), AdvancedExpander(model));

    static Element DownloadCard() => SetupText.Card(
        Loc.Get(Strings.Setup.LocalPlayback.CardTitle),
        Strings.Setup.LocalPlayback.CardSub(RuntimeInformation.ProcessArchitecture.ToString()),
        Icons.Download);

    static Element AdvancedExpander(PlaybackRuntimeSetupModel model) => SettingsExpander.Create(new SettingsExpander.Options
    {
        Header = Loc.Get(Strings.Setup.LocalPlayback.Advanced),
        HeaderIcon = Icons.Settings,
        Items =
        [
            SettingsExpander.Item(Loc.Get(Strings.Setup.LocalPlayback.AdvancedDll), Loc.Get(Strings.Setup.LocalPlayback.AdvancedDllSub),
                isClickEnabled: true, onClick: model.PickFolder),
            SettingsExpander.Item(Loc.Get(Strings.Setup.LocalPlayback.AdvancedInstalled), Loc.Get(Strings.Setup.LocalPlayback.AdvancedInstalledSub),
                isClickEnabled: true, onClick: model.UseInstalled),
            SettingsExpander.Item(Loc.Get(Strings.Setup.LocalPlayback.AdvancedVersion), Loc.Get(Strings.Setup.LocalPlayback.AdvancedVersionSub),
                isClickEnabled: true, onClick: model.ShowAdvanced),
        ],
    });

    // ── FetchingCatalog / Downloading / Verifying: one progress card each — label/byte text + a 162-wide bar. ───────
    static Element CatalogCard() => SetupText.Card(
        Loc.Get(Strings.Playback.Runtime.CheckingSupport),
        Loc.Get(Strings.Playback.Runtime.ReachingCatalog) + "  ·  " + RuntimeInformation.ProcessArchitecture,
        Icons.Download,
        content: ProgressBar.Indeterminate(width: SetupLayout.ProgressWidth));

    static Element DownloadingCard(PlaybackRuntimeSetupModel model)
    {
        long received = model.Received.Value, total = model.Total.Value;
        Element bar = total > 0
            ? ProgressBar.Determinate(SetupRuntimePresentation.ProgressFraction(received, total), width: SetupLayout.ProgressWidth)
            : ProgressBar.Indeterminate(width: SetupLayout.ProgressWidth);
        return SetupText.Card(
            model.DownloadLabel.Value ?? Loc.Get(Strings.Playback.Runtime.Downloading),
            DownloadBytesText(received, total),
            Icons.Download,
            content: bar);
    }

    static Element VerifyingCard() => SetupText.Card(
        Loc.Get(Strings.Playback.Runtime.Verifying),
        Loc.Get(Strings.Playback.Runtime.VerifyingCaption),
        Icons.Download,
        content: ProgressBar.Indeterminate(width: SetupLayout.ProgressWidth));

    static string DownloadBytesText(long received, long total) => total > 0
        ? $"{received / 1_000_000.0:0.0} / {total / 1_000_000.0:0.0} MB"
        : $"{received / 1_000_000.0:0.0} MB";

    // ── Untrusted: a warning InfoBar over two fact cards (the fingerprint the download DID match, and the signature
    // that couldn't be verified). ───────────────────────────────────────────────────────────────────────────────────
    static Element UntrustedGroup(PlaybackRuntimeSetupModel model)
    {
        var entry = model.ActiveEntry;
        string version = entry is null ? "—" : $"{entry.SpotifyVersion}  ·  {entry.Arch}";
        string hash = entry is null ? "—" : SetupRuntimePresentation.ShortHash(entry.DllSha256);
        return SetupText.Group(
            InfoBar.Create(InfoBarSeverity.Warning, Loc.Get(Strings.Playback.Runtime.SignatureInvalid),
                Loc.Get(Strings.Playback.Runtime.UntrustedBody), isClosable: false),
            SetupText.Card(Loc.Get(Strings.Playback.Runtime.DetailVersion), version),
            SetupText.Card(Loc.Get(Strings.Playback.Runtime.DetailSha256), hash));
    }

    // ── Ready: four detail cards (version / arch / signature / location). ──────────────────────────────────────────
    static Element ReadyGroup(PlaybackRuntimeSetupModel model, SetupBody helper, IOverlayService overlay)
    {
        var status = model.Status;
        var kids = new List<Element>(5);
        if (model.UpToDate.Value) kids.Add(SetupText.Body(Loc.Get(Strings.Playback.Runtime.UpToDate)));
        kids.Add(SetupText.Card(Loc.Get(Strings.Setup.LocalPlayback.Version), status.SpotifyVersion ?? "—"));
        kids.Add(SetupText.Card(Loc.Get(Strings.Setup.LocalPlayback.Architecture), status.Arch?.ToString() ?? "—"));
        kids.Add(SetupText.Card(Loc.Get(Strings.Setup.LocalPlayback.Signature), SetupBody.SignatureSummary(status),
            content: status.SignatureInfo is not null
                ? HyperlinkButton.Create(Loc.Get(Strings.Setup.LocalPlayback.View), () => helper.ShowSignatureDetails(overlay, status))
                : null));
        kids.Add(SetupText.Card(Loc.Get(Strings.Setup.LocalPlayback.Location), status.RuntimePath ?? "—",
            content: HyperlinkButton.Create(Loc.Get(Strings.Setup.LocalPlayback.OpenFolder), () => ShellOpen.RevealInExplorer(status.RuntimePath))));
        return SetupText.Group([.. kids]);
    }

    // ── Failed: an error InfoBar over the same download card (primary = "Try again"). ──────────────────────────────
    static Element FailedGroup(PlaybackRuntimeSetupModel model) => SetupText.Group(
        InfoBar.Create(InfoBarSeverity.Error, Loc.Get(Strings.Playback.Runtime.Missing),
            model.Error.Value ?? Loc.Get(Strings.Playback.Runtime.NoPack), isClosable: false),
        DownloadCard());
}
