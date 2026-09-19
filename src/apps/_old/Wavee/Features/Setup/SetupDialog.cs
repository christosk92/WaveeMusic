using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;

namespace Wavee;

/// <summary>The setup wizard's shell: a raw overlay hosting <see cref="SetupPlate"/> — never <c>ContentDialog</c>,
/// because its plate hard-clamps to 548×756 (FluentGpu.Controls/ContentDialog.cs: <c>MaxW</c>/<c>MaxH</c>) and Rise's
/// own reference plate is 762×490. <see cref="SetupPlate"/> reproduces <c>ContentDialog</c>'s own chrome tokens by
/// hand instead (<see cref="Tok.FillSolidBase"/> plate / <see cref="Tok.FillLayerAlt"/> content region /
/// <see cref="Tok.StrokeCardDefault"/> separator / <see cref="Tok.FillSolidBase"/> footer — see
/// <c>ContentDialog.BuildCardCore</c>), so the wizard reads as the exact same WinUI dialog family at a different
/// size. Mirrors <c>SidebarDesignPicker.Open</c> (Features/Sidebar/SidebarDesignPicker.cs) down to the modal chrome
/// and the close-path discipline.</summary>
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
    /// behind it but Mica) or the shell paints its own via <see cref="SetupSession.Covering"/> (post-auth, the
    /// ordinary dim).</param>
    public static OverlayHandle Open(IOverlayService overlay, Action<Action> post, IAppSettings settings,
        SetupSession session, bool bare)
    {
        var handle = overlay.Open(
            static () => NodeHandle.Null,
            () => Embed.Comp(() => new SetupPlate(session)),
            FlyoutPlacement.BottomCenter,
            // PopupChrome.Modal for BOTH mounts, exactly like SidebarDesignPicker.Open — a Raw chrome would also drop
            // the modal CENTERING and the WinUI dialog open/close motion (scale 1.05→1.0 + fade).
            //
            // ScrimVisual = bare: pre-auth (bare) keeps the engine's own smoke painted over the bare Mica backdrop —
            // there is no live shell behind the dialog, just an empty window, so the built-in scrim tints that and
            // nothing else. Post-auth (!bare) turns the engine scrim OFF: the shell paints its own smoke instead
            // (SetupCoverScrim, Features/Shell/WaveeShell.cs) — the shell, not the popup host, is what's actually
            // behind the plate.
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.Modal, Chrome: PopupChrome.Modal)
                { ScrimVisual = bare });

        // Escape / light-dismiss / programmatic veto — the raw-overlay equivalent of ContentDialog's Closing.Cancel
        // (FluentGpu.Controls/ContentDialog.cs: VetoClosing). The veto is SetupGating.CanDismiss: only a TermsRearm
        // run may be dismissed this way, and only while nothing long-running is in flight. Escape on a FirstRun/
        // Reauth wizard used to close it and leave the user staring at SetupPreAuthRoot's bare titlebar over Mica —
        // no dialog, no shell, no way back in, on a fresh install. The wizard is Wavee's only sign-in surface, so
        // "there is nothing behind it" is literal; the honest exit is "Decline" → SetupSession.Secondary → QuitApp,
        // still offered on the Terms page.
        // The veto is for USER dismissals only (Escape, light-dismiss). A Programmatic close is the session's own
        // RequestClose — Local playback's "Open Wavee", Terms's "Decline", the diagnostics hand-off — and must always
        // go through; vetoing it left the finished wizard sitting on screen with a button that did nothing.
        handle.ClosingAction = cause =>
            cause == OverlayCloseCause.Programmatic || SetupGating.EscapeClosesPlate(false, session.Entry, session.IsBusy);

        // Close teardown — structural, not per-button: EVERY close path (Escape, "Not now", a shutdown-time close,
        // a stray dismiss nobody anticipated) funnels through this ONE action.
        //
        // It deliberately does NOT burn a "deferred" marker. This wizard is MANDATORY — Wavee cannot be used without
        // signing in — so `SetupPending` stays armed until the Ready page calls MarkCompleted, and an abandoned run
        // simply resumes on the next launch. MarkDeferred survives only for a deliberate navigation away (see
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

        // Only a `bare: false` mount covers a live shell — the shell reads Covering to dim behind it. A bare
        // (pre-auth) mount has no shell behind it to cover, so Covering stays None for one.
        if (!bare) SetupSession.Covering.Value = SetupLayout.CoverFor(shellBehind: true);

        session.RequestClose = handle.Close;
        return handle;
    }
}

/// <summary>The dialog's plate — Rise's own <c>ContentDialog</c> chrome at 762×490: a <see cref="Tok.FillLayerAlt"/>
/// content region (the page host, padded 24 all round) over a 1-px <see cref="Tok.StrokeCardDefault"/> separator over
/// an 80-tall <see cref="Tok.FillSolidBase"/> footer, on a <see cref="Tok.FillSolidBase"/> plate with
/// <see cref="Radii.OverlayAll"/> corners and a <see cref="Tok.StrokeSurfaceDefault"/> hairline. A 30×30 back button
/// (shown per <see cref="SetupCommands.Resolve"/>'s <c>ShowBack</c>) overlays the content region's top-left corner.
/// Enter → primary (when enabled), Backspace → back (when shown) — the <c>ContentDialog.OnCardKey</c> shape
/// (FluentGpu.Controls/ContentDialog.cs), since a raw overlay gives us no default-button handling of its own.</summary>
sealed class SetupPlate : Component
{
    readonly SetupSession _session;
    public SetupPlate(SetupSession session) => _session = session;

    public override Element Render()
    {
        var viewport = UseContextSignal(Viewport.Size);
        float plateW = SetupLayout.Width(viewport.Value.Width);
        float plateH = SetupLayout.Height(viewport.Value.Height);

        var row = SetupCommands.Resolve(_session.BuildCtx());

        // Post-auth only (a bare pre-auth mount never sets Covering away from None — see SetupDialog.Open): re-derive
        // on every page change rather than assuming it can't move once set. Write in an effect, never in render.
        var page = _session.Page.Value;
        UseEffect(() =>
        {
            if (SetupSession.Covering.Peek() != SetupCover.None)
                SetupSession.Covering.Value = SetupLayout.CoverFor(shellBehind: true);
        }, (int)page);

        void OnPlateKey(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && row.PrimaryEnabled) { _session.Primary(); e.Handled = true; }
            else if (e.KeyCode == Keys.Back && row.ShowBack) { _session.Back(); e.Handled = true; }
        }

        Element contentRegion = new BoxEl
        {
            Grow = 1f, Shrink = 1f, MinHeight = 0f,
            Fill = Tok.FillLayerAlt,   // ContentDialogTopOverlay
            Padding = Edges4.All(SetupLayout.PlatePadding),
            Children = [PagesHost()],
        };

        Element chrome = new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f,
            Children =
            [
                contentRegion,
                new BoxEl { Height = 1f, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeCardDefault },
                Embed.Comp(() => new SetupWizardFooter(_session)),
            ],
        };

        var layers = new List<Element>(2) { chrome };
        if (row.ShowBack) layers.Add(BackOverlay());

        return new BoxEl
        {
            ZStack = true,
            Width = plateW, Height = plateH, MinWidth = SetupLayout.MinPlateWidth, MinHeight = SetupLayout.MinPlateHeight,
            Corners = Radii.OverlayAll,
            Fill = Tok.FillSolidBase,
            BorderWidth = 1f, BorderColor = Tok.StrokeSurfaceDefault,
            Shadow = Elevation.Dialog,
            ClipToBounds = true,
            OnKeyDown = OnPlateKey,
            Children = layers.ToArray(),
        };
    }

    // The back button sits over the content region's own 24-DIP padding corner — the same top-left offset the icon
    // column/header start from, so it reads as part of that row rather than floating independently.
    Element BackOverlay() => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Start, Justify = FlexJustify.Start,
        Padding = Edges4.All(SetupLayout.PlatePadding), HitTestPassThrough = true,
        Children = [IconButton.Create(Icons.Back, _session.Back,
            style: IconButton.DefaultStyle with { Size = SetupLayout.BackButtonSize, GlyphSize = SetupLayout.BackGlyphSize })],
    };

    // Grow+Shrink+MinWidth+MinHeight give the keep-alive a DEFINITE box so ClipToBounds has something to clip
    // against — the ContentHost.cs:70-76 recipe. Without it, KeepAliveEl carries no layout columns of its own
    // (Reconciler.cs WriteColumns never runs for it), so it and the ComponentEl chain underneath (SetupPagePlaceholders
    // → SetupPageHost.Frame → the page's ScrollView) shrink-wrap to CONTENT height instead of being bounded by this
    // box: the ScrollView's height becomes its content height, nothing scrolls, and the page's last row clips instead
    // of scrolling into view.
    Element PagesHost() => new BoxEl
    {
        Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true,
        Children =
        [
            Flow.KeepAlive(
                () => _session.Page.Value,
                page => "setup:page:" + (int)page,
                page => SetupPagePlaceholders.For(page),
                new KeepAliveOptions(
                    MaxEntries: 3,
                    TransitionFor: (_, _) => PageNavMotion.RecipeFor(_session.Dir.Peek()),
                    SuppressLayoutTransitionsOnActivation: true)),
        ],
    };
}

/// <summary>The dialog's ONE footer — Rise's <c>ControlGrid</c>: a 210-wide progress column (secondary label + a
/// 162-wide <c>ProgressBar</c>, collapsing to 0 below the icon breakpoint) then two STRETCH buttons, PRIMARY LEFT /
/// SECONDARY RIGHT (Rise's own column order — WinUI <c>ContentDialog</c> puts the default button first). Reads
/// <see cref="SetupCommands.Resolve"/> off <see cref="SetupSession.BuildCtx"/> and nothing else — mirrors
/// <c>PlaybackRuntimeSetupCard.SetupFooter</c> (Features/Shell/PlaybackRuntimeSetupCard.cs). Named
/// <c>SetupWizardFooter</c> (not <c>SetupFooter</c>) because that name is already taken in this namespace by
/// <c>PlaybackRuntimeSetupCard</c>'s own footer.</summary>
sealed class SetupWizardFooter : Component
{
    const float ButtonH = 32f;

    readonly SetupSession _session;
    public SetupWizardFooter(SetupSession session) => _session = session;

    public override Element Render()
    {
        var viewport = UseContextSignal(Viewport.Size);
        bool large = SetupLayout.ShowsIcon(viewport.Value.Width);

        var page = _session.Page.Value;
        var row = SetupCommands.Resolve(_session.BuildCtx());
        var stepNum = SetupGating.StepNumber(page);
        string label = stepNum is { } n ? Strings.Setup.StepOf(n.Step, n.Total) : Loc.Get(Strings.Setup.PreSetup);

        var kids = new List<Element>(3);
        if (large)
            kids.Add(new BoxEl
            {
                // Height-pinned to the button lane and centred inside it, so the column can never stand taller than
                // the lane whatever its children measure. Rise's `Padding="0,0,48,0"` is a RIGHT pad: Edges4 is
                // (Left, Top, Right, Bottom) — passing 48 as the second argument made it a TOP pad, which pushed the
                // label and bar 48 DIP below the lane (the bar past the plate's bottom edge). The engine gate
                // `gate.layout.footer-band` (FluentGpu.VerticalSlice LayoutShellSuite) lays out this exact shape.
                Width = SetupLayout.ProgressColumnWidth, Height = ButtonH, Shrink = 0f, Direction = 1,
                Padding = new Edges4(0f, 0f, SetupLayout.ProgressColumnRightPad, 0f),
                Gap = SetupLayout.ProgressStackGap, Justify = FlexJustify.Center, AlignItems = FlexAlign.Start, AlignSelf = FlexAlign.Center,
                Children =
                [
                    new TextEl(label) { Size = 14f, Weight = 600, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    ProgressBar.Determinate(SetupGating.Progress(page), width: SetupLayout.ProgressWidth),
                ],
            });

        // Primary LEFT, secondary RIGHT (Rise's ControlGrid columns 1/2) — both stretch. A null SecondaryKey (only
        // Ready) lets the primary span the whole action lane instead of leaving a dead second slot.
        kids.Add(PrimaryButton(row.PrimaryKind, Loc.Get(row.PrimaryKey!), _session.Primary, row.PrimaryEnabled));
        if (row.SecondaryKey is { } secondaryKey)
            kids.Add(SecondaryButton(Loc.Get(secondaryKey), _session.Secondary, row.SecondaryEnabled));

        return new BoxEl
        {
            Height = SetupLayout.FooterHeight, Shrink = 0f,
            Padding = Edges4.All(SetupLayout.FooterPadding),
            Fill = Tok.FillSolidBase,
            Direction = 0, Gap = SetupLayout.FooterColumnGap, AlignItems = FlexAlign.Center,
            Children = kids.ToArray(),
        };
    }

    static BoxEl SecondaryButton(string label, Action onClick, bool enabled) =>
        Button.Standard(label, onClick, isEnabled: enabled) with
        { Grow = 1f, Basis = 0f, MinWidth = 0f, Shrink = 1f, Height = ButtonH, MinHeight = ButtonH, Justify = FlexJustify.Center };

    // The primary is the stock WinUI AccentButtonStyle on every page — Rise has no brand-coloured primary, and the
    // Spotify-green override the SignIn page used to carry read as a harsh, off-palette slab in dark mode.
    static BoxEl PrimaryButton(SetupButtonKind kind, string label, Action onClick, bool enabled) => kind switch
    {
        SetupButtonKind.Standard => Button.Standard(label, onClick, isEnabled: enabled)
            with { Grow = 1f, Basis = 0f, MinWidth = 0f, Shrink = 1f, Height = ButtonH, MinHeight = ButtonH, Justify = FlexJustify.Center },
        _ => Button.Accent(label, onClick, isEnabled: enabled)
            with { Grow = 1f, Basis = 0f, MinWidth = 0f, Shrink = 1f, Height = ButtonH, MinHeight = ButtonH, Justify = FlexJustify.Center },
    };
}
