using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Backend.Audio;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Page 8 · Done (<c>data-step="8"</c>) — the second Zune bookend, and now the ONLY body it has. The old
/// "Applying" pane (a step list gated on <c>SetupSession.Apply</c>, which in practice never leaves <c>Idle</c>
/// before jumping straight to <c>Done</c> — see <c>SetupSession.PrimaryDone</c>) is gone. Every required choice was
/// persisted on its own page, and background sidebar/library/playback work continues behind the shell regardless of
/// what this page shows, so the summary — kicker, headline, lead, the six confirmation chips, fine print — is simply
/// always what's on screen. The stage-column (or, narrower than Wide, chip-adjacent) checklist reports the SAME real
/// observables honestly (<see cref="SetupDoneSteps"/>) instead of a synthetic four-stage apply counter.</summary>
sealed class SetupDonePage : Component
{
    public override Element Render()
    {
        var session = SetupSession.Current;
        var svc = UseContext(Services.Slot);
        var settings = svc?.Settings;
        var bridge = UseContext(PlaybackBridge.Slot);
        var viewport = UseContextSignal(Viewport.Size);
        var post = UsePost();

        // Library sync idleness: no real sync service at all counts as already idle (nothing to wait for);
        // otherwise wait — once — for the one real signal that means "caught up".
        var libraryIdle = UseSignal(svc?.RealSync is null);
        UseEffect(() =>
        {
            if (svc?.RealSync is { } sync && !libraryIdle.Peek())
                sync.WaitForIdleAsync().ContinueWith(_ => post(() => libraryIdle.Value = true));
            return null;
        }, default);

        var outcome = bridge?.RuntimeStatus.Value.Outcome ?? ProvisioningOutcome.NeverAttempted;   // subscribes
        bool runtimeDeclined = session?.RuntimeDeclined ?? false;
        var stepStates = SetupDoneSteps.Compute(svc?.RealSync is not null, libraryIdle.Value, runtimeDeclined, outcome);

        float plateW = SetupLayout.PlateWidth(viewport.Value.Width);
        var tierSig = UseSignal(SetupLayout.NominalTierFor(plateW));
        UseEffect(() =>
        {
            var current = tierSig.Peek();
            var next = SetupLayout.TierFor(plateW, current);
            if (next != current) tierSig.Value = next;
        }, plateW);
        bool wide = SetupLayout.ShowsHero(tierSig.Value);

        var stepRows = new (string Label, SetupStepState State)[]
        {
            (Loc.Get(Strings.Setup.Done.StepSettings), stepStates[0]),
            (Loc.Get(Strings.Setup.Done.StepSidebar), stepStates[1]),
            (Loc.Get(Strings.Setup.Done.StepLibrary), stepStates[2]),
            (Loc.Get(Strings.Setup.Done.StepRuntime), stepStates[3]),
        };

        Element pane = SummaryPane(settings, session, bridge, wide, wide ? null : stepRows) with
        {
            Key = "done:summary",
            Enter = new EnterExit(Dy: 6f, Opacity: 0f, Active: true),
            Exit = new EnterExit(Dy: -4f, Opacity: 0f, Active: true),
        };

        Element? stage = wide
            ? SetupStage.Column(
                SetupStage.Rail(SetupPage.Done, 220f),
                SetupStepList.Column(stepRows),
                SetupStage.Spacer(),
                SetupStage.Caption(Loc.Get(Strings.Setup.Done.StageCaptionTitle), Loc.Get(Strings.Setup.Done.StageCaptionSub)))
            : null;

        return SetupPageHost.Frame(SetupPage.Done, "", "", pane, pinnedHeader: false, stage: stage, scrollBody: false);
    }

    // ── Summary — the Zune bookend: kicker, "You're **in**." (mixed weight), a lead, the chip row, fine print. ───────
    // `narrowSteps` is non-null only below Wide (no stage column to host the checklist), appended right under the
    // chips so nothing becomes unreachable once the stage drops.
    static Element SummaryPane(IAppSettings? settings, SetupSession? session, PlaybackBridge? bridge, bool wide,
        (string Label, SetupStepState State)[]? narrowSteps)
    {
        System.Func<TextSpan[], SpanTextEl> headlineBuilder = wide ? SetupType.Display : SetupType.Small;

        // The prototype personalizes this line ("You're **in**, Christos."). The display name is whatever the real
        // profile reported, so read it live — it arrives with the Finalizing→Authenticated snapshot, which can land
        // while this page is already mounted. The comma and the full stop live INSIDE the loc value, not concatenated
        // here: ", {name}." punctuation and word order are the translator's call.
        string? who = bridge?.Login.Value.User?.DisplayName;
        string suffix = string.IsNullOrWhiteSpace(who)
            ? Loc.Get(Strings.Setup.Done.HeadlineSuffixPlain)
            : Strings.Setup.Done.HeadlineSuffixNamed(who!);

        Element headline = headlineBuilder(
        [
            new TextSpan(Loc.Get(Strings.Setup.Done.HeadlinePrefix)),
            new TextSpan(Loc.Get(Strings.Setup.Done.HeadlineBold), Weight: 600),
            new TextSpan(suffix),
        ]) with { MaxWidth = 480f };

        var children = new List<Element>
        {
            new TextEl(Loc.Get(Strings.Setup.Complete))
            {
                Size = 11f, Weight = 600, CharSpacing = WaveeType.EyebrowTracking, Color = Tok.AccentTextPrimary,
                Margin = new Edges4(0f, 0f, 0f, 14f),
            },
            headline,
            SetupRows.Lead(Loc.Get(Strings.Setup.Done.Lead)) with { MaxWidth = 480f, Margin = new Edges4(0f, 0f, 0f, 18f) },
            new BoxEl { Direction = 0, Wrap = true, Gap = Spacing.S, Children = BuildChips(settings, session, bridge) },
        };

        if (narrowSteps is { Length: > 0 })
            children.Add(new BoxEl { Margin = new Edges4(14f, 0f, 0f, 0f), Children = [SetupStepList.Column(narrowSteps)] });

        children.Add(SetupCompact.FinePrint(Loc.Get(Strings.Setup.Done.Fine)) with { Margin = new Edges4(14f, 0f, 0f, 0f) });

        return new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f, Justify = FlexJustify.Center,
            Children = children.ToArray(),
        };
    }

    static Element[] BuildChips(IAppSettings? settings, SetupSession? session, PlaybackBridge? bridge)
    {
        if (settings is null) return [];

        string themeLabel = settings.Get(WaveeSettings.ThemeMode) switch
        {
            1 => Loc.Get(Strings.Settings.Choice.Light),
            2 => Loc.Get(Strings.Settings.Choice.Dark),
            _ => Loc.Get(Strings.Settings.Choice.System),
        };
        string sidebarLabel = (SidebarDesign)settings.Get(WaveeSettings.SidebarDesign) switch
        {
            SidebarDesign.Classic => Loc.Get(Strings.Sidebar.Design.Classic),
            SidebarDesign.LibraryV3 => Loc.Get(Strings.Sidebar.Design.V3),
            _ => Loc.Get(Strings.Sidebar.Design.Custom),
        };
        string qualityLabel = settings.Get(WaveeSettings.PlaybackQuality) switch
        {
            0 => Loc.Get(Strings.Settings.Playback.QualityNormal),
            1 => Loc.Get(Strings.Settings.Playback.QualityHigh),
            _ => Loc.Get(Strings.Settings.Playback.QualityVeryHigh),
        };
        bool crossfade = settings.Get(WaveeSettings.CrossfadeEnabled);
        bool notifyWindows = settings.Get(WaveeSettings.NotifyWindows);
        bool runtimeOn = !(session?.RuntimeDeclined ?? false) && (bridge?.RuntimeStatus.Value.IsReady ?? false);

        return
        [
            Chip(true, themeLabel),
            Chip(true, sidebarLabel),
            Chip(true, qualityLabel),
            Chip(crossfade, Loc.Get(Strings.Settings.Sound.Crossfade)),
            Chip(runtimeOn, Loc.Get(Strings.Playback.Runtime.Title)),
            Chip(notifyWindows, Loc.Get(Strings.Settings.Notify.Windows)),
        ];
    }

    static Element Chip(bool on, string label) => new BoxEl
    {
        Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center, Height = 28f, Shrink = 0f,
        Padding = new Edges4(10f, 0f, 10f, 0f), Corners = CornerRadius4.All(14f),
        Fill = Tok.FillCardSecondary,
        Children =
        [
            new BoxEl { Width = 9f, Height = 9f, Corners = CornerRadius4.All(4.5f), Fill = on ? Tok.AccentDefault : Tok.TextTertiary },
            new TextEl(label) { Size = 12.5f, Color = Tok.TextSecondary },
        ],
    };
}
