using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;

namespace Wavee;

/// <summary>The setup wizard's shell: a raw overlay hosting <see cref="SetupPlate"/> — never <c>ContentDialog</c>,
/// because its plate hard-clamps to 548×756 (FluentGpu.Controls/ContentDialog.cs: <c>MaxW</c>/<c>MaxH</c>) and this
/// plate is 896×576. Mirrors <c>SidebarDesignPicker.Open</c> (Features/Sidebar/SidebarDesignPicker.cs) down to the
/// modal chrome and the close-path discipline.</summary>
static class SetupDialog
{
    /// <param name="overlay">The ambient overlay service (<c>UseContext(Overlay.Service)</c> at the call site).</param>
    /// <param name="post">The UI-thread post (<c>UsePost()</c> at the call site) — unused by this step's placeholder
    /// pages, carried now so later steps (LocalPlayback's download progress, SignIn's device-code poll) never need a
    /// signature change to get it.</param>
    /// <param name="settings">The store <see cref="SetupGating.MarkDeferred"/> burns the one-time marker into, from
    /// <c>handle.ClosedAction</c> below — the ONE close funnel every exit path lands on.</param>
    /// <param name="bare">True for the pre-auth mount (no real shell exists yet), false post-auth. Both use
    /// <see cref="PopupChrome.Modal"/>; this selects whether the engine's own popup scrim paints (bare — nothing
    /// behind it but Mica) or the shell paints its own via <see cref="SetupSession.Covering"/> (post-auth, where the
    /// cover can be a plain dim OR a lifted, live-preview page).</param>
    public static OverlayHandle Open(IOverlayService overlay, Action<Action> post, IAppSettings settings,
        SetupSession session, bool bare)
    {
        var handle = overlay.Open(
            static () => NodeHandle.Null,
            () => Embed.Comp(() => new SetupPlate(session)),
            FlyoutPlacement.BottomCenter,
            // PopupChrome.Modal for BOTH mounts, exactly like SidebarDesignPicker.Open. An earlier revision used
            // PopupChrome.Raw for the pre-auth mount to avoid a scrim — that was wrong twice over: `Raw` means no
            // chrome AT ALL, so it also dropped the modal CENTERING (the plate rendered pinned to the window's
            // top-left) and the WinUI dialog open/close motion (scale 1.05→1.0 + fade). (`bare` still selects whether
            // the SHELL gets dimmed — see Covering below — it just has no business choosing the chrome.)
            //
            // ScrimVisual = bare: pre-auth (bare) keeps the engine's own smoke painted over the bare Mica backdrop —
            // there is no live shell behind the dialog, just an empty window, so the built-in scrim tints that and
            // nothing else. Post-auth (!bare) turns the engine scrim OFF: the shell paints its own smoke instead
            // (SetupCoverScrim, Features/Shell/WaveeShell.cs), because it — not the popup host — is the one that
            // knows whether the current page wants a live, un-dimmed preview (Appearance/Sidebar) or the ordinary dim.
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.Modal, Chrome: PopupChrome.Modal)
                { ScrimVisual = bare });

        // Escape / light-dismiss / programmatic veto — the raw-overlay equivalent of ContentDialog's Closing.Cancel
        // (FluentGpu.Controls/ContentDialog.cs: VetoClosing). TWO vetoes, both in SetupGating.CanDismiss:
        //   • a long-running step is in flight (Apply, or a catalog/download/verify), and
        //   • this is not a RERUN. Escape on a FirstRun/Reauth wizard used to close it and leave the user staring at
        //     SetupPreAuthRoot's bare titlebar over Mica — no dialog, no shell, no way back in, on a fresh install.
        //     The wizard is Wavee's only sign-in surface, so "there is nothing behind it" is literal; the honest exit
        //     is "Not now" → SetupSession.Secondary → QuitApp, which is still offered on every page.
        // A THIRD case rides in front of both: a page with an in-place disclosure open (Terms → full agreement)
        // registers it as session.EscapeConsumer, and an Escape is spent closing THAT — the overlay host offers
        // Escape to this modal before any focused node sees it, so the disclosure can never catch the key itself.
        // The veto is for USER dismissals only (Escape, light-dismiss). A Programmatic close is the session's own
        // RequestClose — Done's "Open Wavee", Welcome's "Not now", the diagnostics hand-off — and must always go
        // through; vetoing it left the finished wizard sitting on screen with a button that did nothing.
        handle.ClosingAction = cause =>
        {
            if (cause == OverlayCloseCause.Programmatic) return true;
            bool nested = cause == OverlayCloseCause.Escape && session.EscapeConsumer is not null;
            if (nested) session.EscapeConsumer!.Invoke();
            return SetupGating.EscapeClosesPlate(nested, session.IsRerun, session.IsBusy);
        };

        // Close teardown — structural, not per-button: EVERY close path (Escape, "Not now", a shutdown-time close,
        // a stray dismiss nobody anticipated) funnels through this ONE action.
        //
        // It deliberately does NOT burn a "deferred" marker. This wizard is MANDATORY — Wavee cannot be used without
        // signing in — so `SetupPending` stays armed until the Done page calls MarkCompleted, and an abandoned run
        // simply resumes on the next launch. That is the opposite of SidebarDesignPicker's discipline, and the
        // difference is the point: its chooser is OPTIONAL, so "a one-time dialog that comes back is a failure mode"
        // holds there. Applying that reasoning here (an earlier revision did) was a real bug — clearing the marker
        // dropped the user out of the only sign-in surface Wavee has, permanently, because the wizard then never
        // returned. MarkDeferred survives only for a deliberate navigation away (see
        // PlaybackRuntimeSetupModel.OpenDiagnostics), where the user is already signed in and has somewhere to be.
        handle.ClosedAction = () =>
        {
            // A bare pre-auth overlay never set Covering. Leaving it untouched also prevents its teardown callback
            // from clearing the post-auth overlay's freshly-set shell cover if the two hosts overlap for one frame.
            if (!bare) SetupSession.Covering.Value = SetupCover.None;
            // An Authenticated flip replaces SetupPreAuthRoot with WaveeShell, which necessarily destroys this bare
            // overlay. That is a host handoff, not a dismissal: preserve the unfinished session so SetupChrome can
            // remount it on LocalPlayback. Every ordinary/post-auth close still clears the session exactly once.
            bool authHandoff = SetupGating.CarriesAcrossAuthGate(
                bare,
                SetupGating.IsPending(settings),
                session.Bridge?.Auth.Peek() == Wavee.Core.AuthStatus.Authenticated);
            if (!authHandoff && ReferenceEquals(SetupSession.Current, session)) SetupSession.Current = null;
            SetupSession.BumpMarker();   // let WaveeApp's login gate re-evaluate IsPending right now (see MarkerEpoch)
        };

        // Only a `bare: false` mount covers a live shell — the shell reads Covering to dim (or, on Appearance/
        // Sidebar, stay a live preview) behind it. A bare (pre-auth) mount has no shell behind it to cover, so
        // Covering stays None for one.
        if (!bare) SetupSession.Covering.Value = SetupLayout.CoverFor(session.Page.Peek(), shellBehind: true);

        session.RequestClose = handle.Close;
        return handle;
    }
}

/// <summary>The dialog's plate: sized off the viewport (<see cref="SetupLayout"/>), a back button (top-left,
/// shown per <see cref="SetupCommandRow.ShowBack"/>), the keep-alive page host, a hairline, then the one footer.
/// Enter → primary (when enabled), Backspace → back (when shown) — the <c>ContentDialog.OnCardKey</c> shape
/// (FluentGpu.Controls/ContentDialog.cs), since a raw overlay gives us no default-button handling of its own.</summary>
sealed class SetupPlate : Component
{
    readonly SetupSession _session;
    public SetupPlate(SetupSession session) => _session = session;

    public override Element Render()
    {
        var viewport = UseContextSignal(Viewport.Size);
        float plateW = SetupLayout.PlateWidth(viewport.Value.Width);
        float plateH = SetupLayout.PlateHeight(viewport.Value.Height);

        var row = SetupCommands.Resolve(_session.BuildCtx());

        // Post-auth only (a bare pre-auth mount never sets Covering away from None — see SetupDialog.Open): the page
        // the user is looking at can change what "cover the shell" means (a plain dim vs. lifting it entirely for a
        // live-preview page like Appearance/Sidebar), so re-derive it on every page change. Write in an effect, never
        // in render.
        var page = _session.Page.Value;
        UseEffect(() =>
        {
            if (SetupSession.Covering.Peek() != SetupCover.None)
                SetupSession.Covering.Value = SetupLayout.CoverFor(page, shellBehind: true);
        }, (int)page);

        void OnPlateKey(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && row.PrimaryEnabled) { _session.Primary(); e.Handled = true; }
            else if (e.KeyCode == Keys.Back && row.ShowBack) { _session.Back(); e.Handled = true; }
        }

        Element chrome = new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f,
            Children =
            [
                PagesHost(),
                new BoxEl { Height = 1f, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeCardDefault },
                Embed.Comp(() => new SetupWizardFooter(_session)),
            ],
        };

        var layers = new List<Element>(2) { chrome };
        if (row.ShowBack) layers.Add(BackOverlay());

        return new BoxEl
        {
            ZStack = true,
            Width = plateW, Height = plateH, MinWidth = SetupLayout.MinWidth, MinHeight = SetupLayout.MinHeight,
            Corners = Radii.OverlayAll,
            Fill = Tok.FillSolidBase,
            BorderWidth = 1f, BorderColor = Tok.StrokeSurfaceDefault,
            Shadow = Elevation.Dialog,
            ClipToBounds = true,
            OnKeyDown = OnPlateKey,
            Children = layers.ToArray(),
        };
    }

    Element BackOverlay() => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Start, Justify = FlexJustify.Start,
        Padding = new Edges4(Spacing.M, Spacing.M, 0f, 0f), HitTestPassThrough = true,
        Children = [IconButton.Create(Icons.Back, _session.Back, size: ControlSize.Small)],
    };

    Element PagesHost() => new BoxEl
    {
        Grow = 1f, Shrink = 1f, MinHeight = 0f, ClipToBounds = true,
        Children =
        [
            Flow.KeepAlive(
                () => _session.Page.Value,
                page => "setup:page:" + (int)page,
                page => SetupPagePlaceholders.For(page),
                new KeepAliveOptions(
                    MaxEntries: 9,
                    TransitionFor: (_, _) => PageNavMotion.RecipeFor(_session.Dir.Peek()),
                    SuppressLayoutTransitionsOnActivation: true)),
        ],
    };
}

/// <summary>The dialog's ONE command row. Reads <see cref="SetupCommands.Resolve"/> off <see cref="SetupSession.BuildCtx"/>
/// and nothing else — mirrors <c>PlaybackRuntimeSetupCard.SetupFooter</c> (Features/Shell/PlaybackRuntimeSetupCard.cs):
/// a left step label + progress, then secondary, then primary. Named <c>SetupWizardFooter</c> (not <c>SetupFooter</c>)
/// because that name is already taken in this namespace by <c>PlaybackRuntimeSetupCard</c>'s own footer.</summary>
sealed class SetupWizardFooter : Component
{
    const float ButtonH = 32f;

    readonly SetupSession _session;
    public SetupWizardFooter(SetupSession session) => _session = session;

    public override Element Render()
    {
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

        var page = _session.Page.Value;
        var row = SetupCommands.Resolve(_session.BuildCtx());
        var stepNum = SetupGating.StepNumber(page);
        string label = stepNum is { } n ? Strings.Setup.StepOf(n.Step, n.Total) : Loc.Get(SetupGating.StepLabelKey(page)!);

        var progress = new BoxEl
        {
            Direction = 1, Gap = Spacing.XS, Shrink = 0f, MinWidth = 0f, Justify = FlexJustify.Center,
            Children =
            [
                new TextEl(label) { Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ProgressBar.Determinate(SetupGating.Progress(page), width: SetupLayout.ProgressWidth),
            ],
        };

        float actionFallback = MathF.Max(0f, plateW - 2f * Spacing.XXL
            - (SetupLayout.StacksFooter(tier) ? 0f : SetupLayout.ProgressLaneWidth + Spacing.S));
        string actionsKey = $"setup:actions:{(int)page}:{row.PrimaryKey}:{row.SecondaryKey}:{row.PrimaryEnabled}:{row.SecondaryEnabled}:{(int)tier}";
        Element actions = Responsive.Of(
            width => ActionCluster(width, row, tier),
            fallback: actionFallback,
            grow: SetupLayout.StacksFooter(tier) ? 0f : 1f) with { Key = actionsKey };

        Element content;
        if (SetupLayout.StacksFooter(tier))
        {
            content = new BoxEl
            {
                Direction = 1, Gap = Spacing.S, Grow = 1f, MinWidth = 0f,
                Children = [progress, actions],
            };
        }
        else
        {
            content = new BoxEl
            {
                Direction = 0, Gap = Spacing.S, Grow = 1f, MinWidth = 0f, AlignItems = FlexAlign.Center,
                Children = [progress with { Width = SetupLayout.ProgressLaneWidth }, actions],
            };
        }

        return new BoxEl
        {
            Key = "setup:footer:" + (int)tier,
            Direction = 1, Height = SetupLayout.FooterHeightFor(tier), Shrink = 0f,
            Padding = new Edges4(Spacing.XXL, Spacing.L, Spacing.XXL, Spacing.L),
            Fill = Tok.FillSolidBaseAlt, Children = [content],
        };
    }

    Element ActionCluster(float width, SetupCommandRow row, SetupLayoutTier tier)
    {
        string primary = Loc.Get(row.PrimaryKey!);
        bool hasSecondary = row.SecondaryKey is not null;

        if (SetupLayout.StacksFooterActions(tier))
        {
            var stacked = new List<Element>(2);
            if (row.SecondaryKey is { } secondaryKey)
                stacked.Add(SecondaryButton(Loc.Get(secondaryKey), _session.Secondary, row.SecondaryEnabled, width));
            stacked.Add(PrimaryButton(row.PrimaryKind, primary, _session.Primary, row.PrimaryEnabled, width));
            return new BoxEl { Direction = 1, Gap = Spacing.S, MinWidth = 0f, Children = stacked.ToArray() };
        }

        float buttonWidth = hasSecondary ? MathF.Max(0f, (width - Spacing.S) * 0.5f) : width;
        var rowKids = new List<Element>(2);
        if (row.SecondaryKey is { } secondary)
            rowKids.Add(SecondaryButton(Loc.Get(secondary), _session.Secondary, row.SecondaryEnabled, buttonWidth));
        rowKids.Add(PrimaryButton(row.PrimaryKind, primary, _session.Primary, row.PrimaryEnabled, buttonWidth));
        return new BoxEl { Direction = 0, Gap = Spacing.S, MinWidth = 0f, Children = rowKids.ToArray() };
    }

    static BoxEl SecondaryButton(string label, Action onClick, bool enabled, float width) =>
        Button.Standard(label, onClick, isEnabled: enabled) with
        { Width = width, MinWidth = 0f, Shrink = 0f, Height = ButtonH, MinHeight = ButtonH, Justify = FlexJustify.Center };

    static BoxEl PrimaryButton(SetupButtonKind kind, string label, Action onClick, bool enabled, float width) => kind switch
    {
        SetupButtonKind.Spotify => Button.Create(label, onClick, ButtonAppearance.Accent, isEnabled: enabled, palette: SpotifyPalette)
            with { Width = width, MinWidth = 0f, Shrink = 0f, Height = ButtonH, MinHeight = ButtonH, Justify = FlexJustify.Center },
        SetupButtonKind.Standard => Button.Standard(label, onClick, isEnabled: enabled)
            with { Width = width, MinWidth = 0f, Shrink = 0f, Height = ButtonH, MinHeight = ButtonH, Justify = FlexJustify.Center },
        _ => Button.Accent(label, onClick, isEnabled: enabled)
            with { Width = width, MinWidth = 0f, Shrink = 0f, Height = ButtonH, MinHeight = ButtonH, Justify = FlexJustify.Center },
    };

    // The SignIn page's branded primary — the same green as the login takeover's wordmark (LoginView.SpotifyGreen,
    // Features/Auth/LoginView.cs), substituted through Button's PUBLIC color-only override seam (Button.Create's
    // `palette` — FluentGpu.Controls/Button.cs) so the button keeps stock Accent geometry/timing and only the color
    // ramp changes.
    static readonly Button.ButtonPalette SpotifyPalette = new(
        Background: new StateBrush(
            LoginView.SpotifyGreen,
            Shade(LoginView.SpotifyGreen, 0.92f),
            Shade(LoginView.SpotifyGreen, 0.84f),
            LoginView.SpotifyGreen with { A = 0.4f }),
        Foreground: new StateBrush(
            ColorF.FromRgba(255, 255, 255), ColorF.FromRgba(255, 255, 255),
            ColorF.FromRgba(255, 255, 255), ColorF.FromRgba(255, 255, 255, 140)),
        Border: Button.BorderRamp.Flat(GradientSpec.Solid(ColorF.Transparent)),
        Sizing: BackgroundSizing.OuterBorderEdge);

    static ColorF Shade(ColorF c, float f) => c with { R = c.R * f, G = c.G * f, B = c.B * f };
}
