using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Page 3 · Local playback (<c>data-step="3"</c>). REUSES <see cref="PlaybackRuntimeSetupModel"/> wholesale
/// (<c>Features/Shell/PlaybackRuntimeSetupCard.cs</c>) — <see cref="SetupPagePlaceholders"/>'s capture wrapper has
/// already constructed it (<see cref="SetupSession.EnsureRuntime"/>) by the time this page mounts, so the phase
/// this page shows is the exact same <see cref="PlaybackRuntimeSetupModel.PhaseSig"/> the footer reads.
///
/// <para>Stage/decision split (Work package D): the DECISION column (480 DIP) shows the "what step am I on"
/// ladder (<see cref="SetupDecision.StepCard"/> × 3, states from <see cref="SetupRuntimePresentation.StepStates"/>)
/// plus whichever escape-hatch chip row applies; the STAGE column (344 DIP, Wide only) shows the live detail — the
/// same progress bar/fact-box content <c>PlaybackRuntimeSetupCard.SetupBody</c>'s extracted arms
/// (<c>CatalogWaiting</c>/<c>Downloading</c>/<c>Verifying</c>/<c>VerifyDetailBox</c>/<c>ReadyBadge</c>/
/// <c>ReadyDetailBox</c>/<c>ReadyLinks</c>/<c>VersionPicker</c>) already build for the standalone dialog — never a
/// second copy of that formatting. Below Wide, <see cref="SetupPageHost.Frame"/> drops the stage column entirely, so
/// this page appends the stage panel under its own column instead of losing that detail.</para></summary>
sealed class SetupLocalPlaybackPage : Component
{
    static readonly Dictionary<PlaybackRuntimeSetupModel.Phase, string> TitleKeys = new()
    {
        [PlaybackRuntimeSetupModel.Phase.Offer] = Strings.Playback.Runtime.Title,
        [PlaybackRuntimeSetupModel.Phase.FetchingCatalog] = Strings.Playback.Runtime.Title,
        [PlaybackRuntimeSetupModel.Phase.Downloading] = Strings.Playback.Runtime.Title,
        [PlaybackRuntimeSetupModel.Phase.Verifying] = Strings.Playback.Runtime.Title,
        [PlaybackRuntimeSetupModel.Phase.Untrusted] = Strings.Playback.Runtime.SignatureInvalid,
        [PlaybackRuntimeSetupModel.Phase.Ready] = Strings.Playback.Runtime.Ready,
        [PlaybackRuntimeSetupModel.Phase.Failed] = Strings.Playback.Runtime.Title,
        [PlaybackRuntimeSetupModel.Phase.Advanced] = Strings.Playback.Runtime.ChooseVersion,
    };

    const float ReadyLabelWidth = 72f;

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var bridge = UseContext(PlaybackBridge.Slot);
        var overlay = UseContext(Overlay.Service);
        var go = UseContext(HistoryStore.NavCtx);
        var post = UsePost();

        var session = SetupSession.Current;
        var model = session?.Runtime;
        if (model is null && session is not null && svc?.Settings is { } settings0 && bridge is not null)
            model = session.EnsureRuntime(svc, settings0, bridge, post);

        // Tier hysteresis (pattern: SetupPage.SignIn.cs) — this page decides for ITSELF whether to build the wide
        // stage/decision split or fold the stage panel under the column, exactly like the frame does structurally.
        var viewport = UseContextSignal(Viewport.Size);
        float plateW = SetupLayout.PlateWidth(viewport.Value.Width);
        var tierSig = UseSignal(SetupLayout.NominalTierFor(plateW));
        UseEffect(() =>
        {
            var current = tierSig.Peek();
            var next = SetupLayout.TierFor(plateW, current);
            if (next != current) tierSig.Value = next;
        }, plateW);
        var tier = tierSig.Value;
        bool wide = SetupLayout.ShowsHero(tier);
        float barW = SetupLayout.RuntimeBarWidth(tier);

        Element body;
        Element? stage = null;
        string title;
        string? lead = null;
        var phase = PlaybackRuntimeSetupModel.Phase.Offer;

        if (model is null)
        {
            body = SetupCompact.Column(SetupBody.Body(Loc.Get(Strings.Playback.Runtime.NotActive)));
            title = Loc.Get(Strings.Playback.Runtime.Title);
        }
        else
        {
            var helper = new SetupBody(model);
            phase = model.PhaseSig.Value;
            title = Loc.Get(TitleKeys[phase]);
            lead = LeadFor(phase);

            Element finePrint = SetupCompact.FinePrint(Loc.Get(Strings.Setup.LocalPlayback.FinePrint));
            body = SetupDecision.Column(wide, DecisionKids(phase, model, helper, go),
                phase == PlaybackRuntimeSetupModel.Phase.Advanced ? null : finePrint);

            var panelKind = SetupRuntimePresentation.StagePanelFor(phase);
            if (wide)
                stage = Stage(panelKind, phase, model, helper, overlay, barW);
            else
                body = SetupCompact.Column(body, StagePanel(panelKind, phase, model, helper, overlay, barW));
        }

        body = body with
        {
            Key = "runtime:" + phase,
            Enter = new EnterExit(Dy: 6f, Opacity: 0f, Active: true),
            Exit = new EnterExit(Dy: -4f, Opacity: 0f, Active: true),
            Transition = MotionTok.StandardEnter,
        };

        return SetupPageHost.Frame(SetupPage.LocalPlayback, Loc.Get(Strings.Setup.Eyebrow.LocalPlayback), title, body,
            lead: lead, stage: wide ? stage : null, scrollBody: !wide);
    }

    // ── Lead (frame header, under the title) ──────────────────────────────────────────────────────────────────────
    static string? LeadFor(PlaybackRuntimeSetupModel.Phase phase) => phase switch
    {
        PlaybackRuntimeSetupModel.Phase.Ready => Loc.Get(Strings.Setup.LocalPlayback.ReadyLead),
        PlaybackRuntimeSetupModel.Phase.Advanced or PlaybackRuntimeSetupModel.Phase.Untrusted => null,
        _ => Loc.Get(Strings.Setup.LocalPlayback.OfferLead),
    };

    // ── Decision column (480 DIP): the step ladder + whichever escape-hatch chip row applies, driven by the SAME
    // SetupRuntimePresentation predicates SetupRuntimePresentationTests pins — never a second "which phase shows
    // what" table drifting from the tested one. ──────────────────────────────────────────────────────────────────
    static Element[] DecisionKids(PlaybackRuntimeSetupModel.Phase phase, PlaybackRuntimeSetupModel model,
        SetupBody helper, Action<string, string?>? go)
    {
        var kids = new List<Element>();

        if (phase == PlaybackRuntimeSetupModel.Phase.Untrusted)
            kids.Add(SetupBody.Untrusted());
        if (phase == PlaybackRuntimeSetupModel.Phase.Failed)
            kids.Add(helper.Failed());
        if (phase == PlaybackRuntimeSetupModel.Phase.Ready && model.UpToDate.Value)
            kids.Add(SetupBody.Body(Loc.Get(Strings.Playback.Runtime.UpToDate)));
        if (phase == PlaybackRuntimeSetupModel.Phase.Advanced)
        {
            kids.Add(SetupBody.Body(Loc.Get(Strings.Playback.Runtime.AdvancedBody)));
            kids.Add(ScrollView(helper.VersionPicker(header: null)) with
            {
                ContentSized = true, MaxHeight = SetupLayout.VersionListMaxHeight, Shrink = 1f, MinHeight = 0f,
            });
        }

        if (SetupRuntimePresentation.ShowsStepCards(phase))
            kids.AddRange(StepCards(phase));
        // Both lines belong to the predicate: the label and the chip row are ONE disclosure. Without the braces the
        // chip row escaped the `if` and rendered on every phase — a "Choose a folder / Use installed Spotify / Choose
        // a version" row sitting under a live download, offering to start a second, competing install.
        if (SetupRuntimePresentation.ShowsAdvancedChips(phase))
        {
            kids.Add(SetupCompact.SectionLabel(Loc.Get(Strings.Setup.LocalPlayback.Advanced)));
            kids.Add(AdvancedChipRow(model, phase == PlaybackRuntimeSetupModel.Phase.Failed ? go : null));
        }
        if (SetupRuntimePresentation.ShowsLocalSourceChips(phase))
            kids.Add(LocalSourceChipRow(model));

        return kids.ToArray();
    }

    static Element[] StepCards(PlaybackRuntimeSetupModel.Phase phase)
    {
        var s = SetupRuntimePresentation.StepStates(phase);
        return
        [
            SetupDecision.StepCard(1, s.Download,
                Loc.Get(Strings.Setup.LocalPlayback.Step1Title), Loc.Get(Strings.Setup.LocalPlayback.Step1Body)),
            SetupDecision.StepCard(2, s.Verify,
                Loc.Get(Strings.Setup.LocalPlayback.Step2Title), Loc.Get(Strings.Setup.LocalPlayback.Step2Body)),
            SetupDecision.StepCard(3, s.Ready,
                Loc.Get(Strings.Setup.LocalPlayback.Step3Title), Loc.Get(Strings.Setup.LocalPlayback.Step3Body)),
        ];
    }

    /// <summary>Offer/Failed's "Choose a folder / Use installed Spotify / Choose a version" trio.
    /// <paramref name="diagnosticsGo"/> null (Offer, or Failed with no navigation target) omits the fourth
    /// diagnostics chip — a clickable-but-inert link is worse than no link (the same rule the old
    /// SettingsExpander-based disclosure followed).</summary>
    static Element AdvancedChipRow(PlaybackRuntimeSetupModel model, Action<string, string?>? diagnosticsGo)
    {
        var chips = new List<Element>
        {
            SetupDecision.Chip(Icons.Folder, Loc.Get(Strings.Playback.Runtime.InstallFromFolder), model.PickFolder),
            SetupDecision.Chip(Icons.MusicNote, Loc.Get(Strings.Playback.Runtime.UseInstalled), model.UseInstalled),
            SetupDecision.Chip(Icons.List, Loc.Get(Strings.Setup.LocalPlayback.ChipChooseVersion), model.ShowAdvanced),
        };
        if (diagnosticsGo is { } goTo)
            chips.Add(SetupDecision.Chip(Icons.Important, Loc.Get(Strings.Playback.Runtime.ViewDiagnostics),
                () => model.OpenDiagnostics(goTo)));
        return SetupDecision.ChipRow(chips.ToArray());
    }

    /// <summary>Advanced's own two-chip row — no "Choose a version" (the version list is already on screen).</summary>
    static Element LocalSourceChipRow(PlaybackRuntimeSetupModel model) => SetupDecision.ChipRow(
        SetupDecision.Chip(Icons.Folder, Loc.Get(Strings.Playback.Runtime.InstallFromFolder), model.PickFolder),
        SetupDecision.Chip(Icons.MusicNote, Loc.Get(Strings.Playback.Runtime.UseInstalled), model.UseInstalled));

    // ── Stage column (344 DIP, Wide only): hero art + a live detail panel + caption. ─────────────────────────────
    static Element Stage(SetupRuntimeStagePanel panelKind, PlaybackRuntimeSetupModel.Phase phase,
        PlaybackRuntimeSetupModel model, SetupBody helper, IOverlayService overlay, float barWidth) => SetupStage.Column(
        SetupStage.Rail(SetupPage.LocalPlayback, SetupLayout.HeroArtSize) with { Key = "runtime:stage:art" },
        StagePanel(panelKind, phase, model, helper, overlay, barWidth) with
        {
            Key = "runtime:stage:panel:" + panelKind,
            Enter = new EnterExit(Dy: 4f, Opacity: 0f, Active: true),
            Exit = new EnterExit(Dy: -4f, Opacity: 0f, Active: true),
            Transition = MotionTok.StandardEnter,
        },
        SetupStage.Spacer(),
        StageCaption(phase));

    /// <summary>The stage's lower panel content — keyed by <see cref="SetupRuntimeStagePanel"/> so Verifying and
    /// Untrusted (both <see cref="SetupRuntimeStagePanel.Verify"/>) cross-fade instead of remounting.</summary>
    static Element StagePanel(SetupRuntimeStagePanel panelKind, PlaybackRuntimeSetupModel.Phase phase,
        PlaybackRuntimeSetupModel model, SetupBody helper, IOverlayService overlay, float barWidth) => panelKind switch
    {
        SetupRuntimeStagePanel.Progress => phase == PlaybackRuntimeSetupModel.Phase.FetchingCatalog
            ? SetupBody.CatalogWaiting(barWidth)
            : helper.Downloading(barWidth),
        SetupRuntimeStagePanel.Verify => phase == PlaybackRuntimeSetupModel.Phase.Untrusted
            ? helper.VerifyDetailBox()
            : helper.Verifying(barWidth),
        SetupRuntimeStagePanel.Ready => new BoxEl
        {
            Direction = 1, Gap = Spacing.M, Shrink = 0f,
            Children =
            [
                SetupBody.ReadyBadge(),
                helper.ReadyDetailBox(model.Status, overlay, ReadyLabelWidth),
                helper.ReadyLinks(),
            ],
        },
        _ => FactsCard(), // Offer, Failed, Advanced
    };

    static Element StageCaption(PlaybackRuntimeSetupModel.Phase phase) => SetupStage.Caption(
        Loc.Get(Strings.Setup.LocalPlayback.StageTitle),
        phase == PlaybackRuntimeSetupModel.Phase.Untrusted
            ? Loc.Get(Strings.Setup.LocalPlayback.UntrustedStageBody)
            : Loc.Get(Strings.Setup.LocalPlayback.StageBody));

    /// <summary>The static "what gets installed" facts — Source/Checked/Lives in/For — shown while there is no live
    /// download/verify/ready state to report (Offer, Failed, Advanced).</summary>
    static Element FactsCard() => SetupStage.DetailBox(
        SetupBody.RuntimeDetailRow(Loc.Get(Strings.Setup.LocalPlayback.FactSource), Loc.Get(Strings.Setup.LocalPlayback.FactSourceValue)),
        SetupBody.RuntimeDetailRow(Loc.Get(Strings.Setup.LocalPlayback.FactChecked), Loc.Get(Strings.Setup.LocalPlayback.FactCheckedValue)),
        SetupBody.RuntimeDetailRow(Loc.Get(Strings.Setup.LocalPlayback.FactLivesIn), Loc.Get(Strings.Setup.LocalPlayback.FactLivesInValue)),
        SetupBody.RuntimeDetailRow(Loc.Get(Strings.Setup.LocalPlayback.FactFor),
            Strings.Setup.LocalPlayback.FactForValue(RuntimeInformation.ProcessArchitecture.ToString())));
}
