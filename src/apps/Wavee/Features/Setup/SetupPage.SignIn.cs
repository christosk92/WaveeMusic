using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Page 2 · Sign in (<c>data-step="2"</c>). NO NEW STATE: the truth is <see cref="PlaybackBridge.Login"/> +
/// <see cref="PlaybackBridge.Auth"/>, folded by the already-written pure <see cref="SetupCommands.Project"/> into
/// the six <see cref="SetupSignInPhase"/> facets this page's LEFT (stage/decision) columns switch on — the footer
/// reads the exact same two signals through the exact same projection (<see cref="SetupSession.BuildCtx"/>), so
/// neither ever drifts from the other. <see cref="SetupSignInPresentation"/> holds the pure stage/decision facts
/// (pane opacity/interactivity, which cards show, which stage kind) both this page and its own theory tests read.
///
/// <para>At Wide (<see cref="SetupLayout.ShowsHero"/>) the page uses the shared stage+decision composition
/// (<see cref="SetupStage"/>/<see cref="SetupDecision"/>): the STAGE column hosts the live pairing pane while it's
/// still useful (Idle/Busy) or a resolved glyph badge once the flow is done (Done/Failed/Expired/Premium), and the
/// DECISION column hosts the actual choice (browser vs. scan) + an approve-progress preview + the identity/result.
/// Below Wide there is no stage column to spend on the pairing pane, so <see cref="StackedOrRowBody"/> keeps the
/// ORIGINAL "left pane / OR divider / compact QR pane" row (moved verbatim, unchanged) — reusing
/// <see cref="LoginView.OrDivider"/>/<see cref="LoginView.CompactRightPane"/> for the QR column, and the takeover's
/// own <see cref="LoginStepBar"/>/<see cref="LoginStepRow"/> for the busy state.</para></summary>
sealed class SetupSignInPage : Component
{
    public override Element Render()
    {
        var bridge = UseContext(PlaybackBridge.Slot);
        var viewport = UseContextSignal(Viewport.Size);
        var snap = bridge?.Login.Value ?? new LoginSnapshot(LoginPhase.LoggedOut);   // subscribe → re-render on phase change
        var auth = bridge?.Auth.Value ?? AuthStatus.LoggedOut;                       // subscribe → re-render on the auth flip
        var facet = SetupCommands.Project(snap.Phase, snap.Step, auth);

        // Screen-reader live region (UIA): announce the actionable state changes — the pairing code SPELLED OUT (a
        // run of characters, not "WZY5Q6TX", which no synthesizer reads back usefully), and the two terminal errors.
        // Keyed on the raw LoginPhase, not the folded facet: Failed and ChallengeExpired carry different copy, and
        // AwaitingApproval must re-announce when a fresh code replaces an expired one. The announcer is wired by the
        // Windows backend (InputHooks.Announce); null elsewhere → a silent no-op.
        var announce = InputHooks.Current.Default.Announce;
        UseEffect(() =>
        {
            if (announce is null) return;
            switch (snap.Phase)
            {
                case LoginPhase.AwaitingApproval when snap.Challenge is { } c:
                    announce(Loc.Get(Strings.Auth.ScanToLogIn) + ". " + Loc.Get(Strings.Auth.OrGoTo) + " spotify.com/pair, " +
                             Loc.Get(Strings.Auth.EnterCodeColon) + " " + string.Join(" ", c.UserCode.Replace("-", "").ToCharArray()), false);
                    break;
                case LoginPhase.Failed:
                    announce(string.IsNullOrWhiteSpace(snap.Error) ? Loc.Get(Strings.Auth.NetworkError) : snap.Error!, true);
                    break;
                case LoginPhase.ChallengeExpired:
                    announce(Loc.Get(Strings.Auth.CodeExpired), true);
                    break;
            }
        }, (int)snap.Phase);

        var session = SetupSession.Current;
        var activePage = session?.Page.Value ?? SetupPage.SignIn;
        bool needsChallenge = SetupCommands.NeedsPairingChallenge(
            activePage, snap.Phase, snap.Challenge is not null);
        UseEffect(() =>
        {
            if (needsChallenge) session?.RestartCode?.Invoke();
        }, needsChallenge);
        float plateW = SetupLayout.PlateWidth(viewport.Value.Width);
        var tierSig = UseSignal(SetupLayout.NominalTierFor(plateW));
        UseEffect(() =>
        {
            var current = tierSig.Peek();
            var next = SetupLayout.TierFor(plateW, current);
            if (next != current) tierSig.Value = next;
        }, plateW);
        var tier = tierSig.Value;

        // No auto-advance after Authenticated: the Done phase is an "Is this you?" confirmation — the user says
        // Yes (footer primary → next page) or Not me (footer secondary → SetupSession.SwitchAccount → Services.LogoutAsync,
        // which drops back to Idle with a fresh code). Silently moving on the moment a token lands gave a wrong-account
        // sign-in no chance to be noticed.

        bool wide = SetupLayout.ShowsHero(tier);
        Element? stage = null;
        Element body;

        if (wide)
        {
            stage = SetupSignInPresentation.StageKind(facet) == SignInStageKind.Pairing
                ? PairingStage(snap, SetupSignInPresentation.PaneOpacity(facet), SetupSignInPresentation.PaneInteractive(facet))
                : TerminalStage(facet, snap.Error);
            body = DecisionColumn(facet, snap, bridge, session?.StartBrowser) with
            {
                Key = "signin:" + facet,
                Enter = new EnterExit(Dy: 6f, Opacity: 0f, Active: true),
                Exit = new EnterExit(Dy: -4f, Opacity: 0f, Active: true),
                Transition = MotionTok.StandardEnter,
            };
        }
        else
        {
            body = StackedOrRowBody(facet, snap, bridge, session?.StartBrowser, tier);
        }

        return SetupPageHost.Frame(SetupPage.SignIn, Loc.Get(Strings.Setup.Eyebrow.SignIn),
            Loc.Get(facet == SetupSignInPhase.Done ? Strings.Setup.SignIn.IsThisYou : Strings.Setup.SignIn.Title), body,
            lead: Loc.Get(facet == SetupSignInPhase.Done ? Strings.Setup.SignIn.ConfirmLead : Strings.Setup.SignIn.Lead), leadMaxLines: 2,
            stage: wide ? stage : null, scrollBody: !wide);
    }

    // ── Wide: the pairing STAGE (SetupStage.Column) ─────────────────────────────────────────────────────────────────

    /// <summary>Idle/Busy: the live (or fading) pairing pane over a caption. The COLUMN's own key stays the single
    /// constant <c>"signin:stage:pairing"</c> across Idle↔Busy — only the pane's OWN key changes with the challenge
    /// (<c>"signin:challenge:"+UserCode</c>, unchanged from the takeover's own convention) — so
    /// <see cref="LoginCountdown"/>/<see cref="WaitingDots"/> never remount purely because the phase flipped from
    /// Idle to Busy while the same code is still live.</summary>
    static Element PairingStage(LoginSnapshot snap, float paneOpacity, bool paneInteractive)
    {
        // Typed BoxEl (not the abstract Element both LoginView.CompactRightPane/PendingCodePane's DECLARED return
        // type would otherwise erase this to) — AlignSelf/Opacity/HitTestVisible below are leaf-level properties, not
        // on the base Element record, so a `with` that touches them needs the concrete type in hand.
        BoxEl pane = snap.Challenge is { } challenge
            ? LoginView.CompactRightPane(challenge, paneInteractive)
            : PendingCodePane();
        pane = pane with
        {
            Key = snap.Challenge is { } c ? "signin:challenge:" + c.UserCode : "signin:challenge:pending",
            AlignSelf = FlexAlign.Center, Opacity = paneOpacity, HitTestVisible = paneInteractive,
            Enter = new EnterExit(Dy: 4f, Sx: 0.97f, Sy: 0.97f, Opacity: 0f, Active: true),
            Exit = new EnterExit(Dy: -2f, Opacity: 0f, Active: true),
            Transition = MotionTok.StandardEnter,
        };

        // SetupStage.Caption's declared return type is the abstract Element (it always constructs a BoxEl
        // internally) — cast once so the Opacity `with` below compiles against the concrete type.
        var caption = (BoxEl)SetupStage.Caption(Loc.Get(Strings.Setup.SignIn.StageTitle), Loc.Get(Strings.Setup.SignIn.StageBody));
        caption = caption with { Opacity = paneOpacity };

        return SetupStage.Column(pane, caption) with
        {
            Key = "signin:stage:pairing",
            Enter = new EnterExit(Dy: 6f, Opacity: 0f, Active: true),
            Exit = new EnterExit(Dy: -4f, Opacity: 0f, Active: true),
            Transition = MotionTok.StandardEnter,
        };
    }

    /// <summary>Done/Failed/Expired/Premium: the pairing pane is gone (it Exit-animated out when the previous
    /// render's key was <c>"signin:stage:pairing"</c>) in favor of a single resolved <see cref="LoginView.GlyphBadge"/>
    /// + caption, reusing the EXACT SAME (title, body) copy pairs the decision column's own terminal InfoBar shows
    /// (<see cref="FailedLeft"/>/<see cref="ExpiredLeft"/>/<see cref="PremiumLeft"/>) so the stage never disagrees
    /// with the decision column about what happened.</summary>
    static Element TerminalStage(SetupSignInPhase facet, string? error)
    {
        (string glyph, ColorF tint, string title, string sub) = facet switch
        {
            SetupSignInPhase.Done => (Icons.Accept, LoginView.SpotifyGreen,
                Loc.Get(Strings.Setup.SignIn.StageDoneTitle), Loc.Get(Strings.Setup.SignIn.DoneLead)),
            SetupSignInPhase.Failed => (Icons.Cancel, Tok.SystemFillCritical,
                Loc.Get(Strings.Auth.CouldntSignIn),
                string.IsNullOrWhiteSpace(error) ? Loc.Get(Strings.Auth.NetworkError) : error!),
            SetupSignInPhase.Expired => (Icons.Important, Tok.SystemFillCaution,
                Loc.Get(Strings.Auth.CodeExpired), Loc.Get(Strings.Auth.CodeExpiredBody)),
            _ => (Icons.Important, LoginView.GoldTint,
                Loc.Get(Strings.Auth.PremiumTitle), Loc.Get(Strings.Auth.PremiumBody)),
        };
        // LoginView.GlyphBadge's declared return type is the abstract Element too — same cast-then-with as the pane.
        var badge = (BoxEl)LoginView.GlyphBadge(glyph, tint);
        badge = badge with { AlignSelf = FlexAlign.Center };
        return SetupStage.Column(badge, SetupStage.Caption(title, sub)) with
        {
            Key = "signin:stage:terminal:" + facet,
            Enter = new EnterExit(Dy: 6f, Opacity: 0f, Active: true),
            Exit = new EnterExit(Dy: -4f, Opacity: 0f, Active: true),
            Transition = MotionTok.StandardEnter,
        };
    }

    /// <summary>The pairing code is minted asynchronously (deliberately not until the wizard REACHES this page, so
    /// it cannot expire while the user reads Welcome/Terms). Until it lands, show that it is coming — promoted
    /// as-is from the page's original inline pending branch so both the Wide stage and the non-Wide
    /// <see cref="StackedOrRowBody"/> show the identical waiting state.</summary>
    static BoxEl PendingCodePane() => new BoxEl
    {
        Width = SetupLayout.CompactPairingWidth, Shrink = 0f, Direction = 1, AlignItems = FlexAlign.Center,
        Justify = FlexJustify.Center, Gap = Spacing.M, MinHeight = 180f,
        Children =
        [
            ProgressRing.Indeterminate(size: 24f),
            new TextEl(Loc.Get(Strings.Auth.GettingCode))
            {
                Size = 12.5f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxWidth = 176f,
            },
        ],
    };

    // ── Wide: the DECISION column (480 DIP) ─────────────────────────────────────────────────────────────────────────

    /// <summary>The card row's own inner width once the card's <c>Spacing.M</c> padding on both sides comes out of
    /// the 480-DIP decision column, then split into the two-column pending/approve grid with an 8-DIP gap between
    /// them (<c>SetupLayout.RowGap</c>-sized, matching the wizard's own tight rhythm).</summary>
    static readonly float ApproveRowWidth =
        (SetupLayout.DecisionWidth(SetupLayout.TargetWidth) - 2f * Spacing.M - SetupLayout.RowGap * 2f) / 2f;

    /// <summary>Assembles the 480-DIP decision column for every phase, reading <see cref="SetupSignInPresentation"/>
    /// for which pieces show rather than re-deriving the split inline — the theory tests over that class are what
    /// actually pin this page's per-phase shape.</summary>
    static Element DecisionColumn(SetupSignInPhase facet, LoginSnapshot snap, PlaybackBridge? bridge, Action? startBrowser)
    {
        var kids = new List<Element>(5);


        if (SetupSignInPresentation.ShowsOptionCards(facet))
        {
            kids.Add(BrowserOptionCard(startBrowser));
            kids.Add(ScanOptionCard());
            // The dead end this closes: both option cards assume an account already exists, so someone without one had
            // nothing to click and no way forward — the wizard is Wavee's ONLY sign-in surface, and it cannot be
            // dismissed on a first run. Spotify owns sign-up, so this hands off to spotify.com/signup, and the note
            // says the part that would otherwise be discovered only after signing up: Free accounts cannot stream here
            // (LoginPhase.PremiumRequired is the wall they'd hit).
            kids.Add(SignUpLink());
            kids.Add(PremiumNote());
        }

        if (facet == SetupSignInPhase.Busy)
            kids.Add(new TextEl(Loc.Get(Strings.Auth.SigningIn)) { Size = 14f, Weight = 600, Color = Tok.TextPrimary });

        // Busy with no bridge (headless/no backend) has nothing live to preview — the plain status line above already
        // covers that case exactly like the old BusyLeft fallback did, so no card is added at all.
        if (SetupSignInPresentation.ShowsApproveCard(facet) && (facet != SetupSignInPhase.Busy || bridge is not null))
            kids.Add(ApproveCard(facet, bridge));

        if (facet == SetupSignInPhase.Done)
            kids.Add(DoneLeft(snap.User));

        if (facet is SetupSignInPhase.Failed or SetupSignInPhase.Expired or SetupSignInPhase.Premium)
            kids.Add(TerminalInfoBar(facet, snap.Error));

        Element disclaimer = new TextEl(Loc.Get(Strings.Auth.Disclaimer))
            { Size = 11.5f, LineHeight = 17f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxLines = 3, Trim = TextTrim.WordEllipsis };
        return SetupDecision.Column(wide: true, kids: kids, pinnedBottom: disclaimer, leadLines: 2);
    }

    /// <summary>"Don't have a Spotify account? Sign up" — the way OUT of the sign-in page for someone who has no
    /// account yet. <c>AlignSelf.Start</c> because the decision column stretches its children and a full-width
    /// hyperlink reads as a button.</summary>
    static Element SignUpLink() =>
        HyperlinkButton.Create(Loc.Get(Strings.Setup.SignIn.NoAccount), () => LoginView.OpenUrl("https://www.spotify.com/signup"))
            with { AlignSelf = FlexAlign.Start };

    /// <summary>The Premium requirement, stated BEFORE sign-up rather than after — same treatment as the column's
    /// pinned trademark disclaimer (11.5/17 tertiary), because it is fine print, not a warning.</summary>
    static Element PremiumNote() => new TextEl(Loc.Get(Strings.Setup.SignIn.PremiumNote))
        { Size = 11.5f, LineHeight = 17f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.WordEllipsis };

    static Element BrowserOptionCard(Action? startBrowser) => SetupDecision.OptionCard(
        Loc.Get(Strings.Setup.SignIn.BrowserCardTitle),
        Loc.Get(Strings.Setup.SignIn.BrowserCardSub),
        LoginView.SpotifyGreen, Icons.Globe, ColorF.FromRgba(11, 26, 18),
        recommended: true,
        // The card click IS the primary: the exact same delegate SetupSession.PrimarySignIn invokes for Idle
        // (SetupSession.cs ~L193) — Enter already routes to Primary() (SetupDialog.cs), so the card and the footer
        // button can never disagree about what "sign in" does.
        onClick: startBrowser,
        trailing: RecommendedPill());

    static Element ScanOptionCard() => SetupDecision.OptionCard(
        ScanCardTitle(wide: true),
        Loc.Get(Strings.Setup.SignIn.ScanCardSub),
        Tok.FillControlSecondary, Icons.Camera, Tok.TextSecondary,
        recommended: false,
        onClick: null);   // info-only: the SAME pairing code already lives in the stage pane beside it

    /// <summary>The scan card's own title reads differently depending on whether the QR code is actually sitting
    /// "on the left" (the Wide stage column) or not — kept as an explicit function (rather than baked into the two
    /// loc keys' call sites) so a future non-Wide composition of this same card can call it with <c>false</c>
    /// without re-deriving the choice.</summary>
    static string ScanCardTitle(bool wide) => wide
        ? Loc.Get(Strings.Setup.SignIn.ScanCardTitle)
        : Loc.Get(Strings.Setup.SignIn.ScanCardTitleNeutral);

    static Element RecommendedPill() => new BoxEl
    {
        Height = 18f, Shrink = 0f, AlignItems = FlexAlign.Center, Padding = new Edges4(7f, 0f, 7f, 0f),
        Corners = Radii.FullAll, Fill = Tok.AccentSubtle,
        Children = [new TextEl(Loc.Get(Strings.Playback.Runtime.Recommended)) { Size = 10.5f, Weight = 600, Color = Tok.AccentTextPrimary }],
    };

    /// <summary>Idle: a PENDING preview of the four finalizing steps (a static render — NOT the live
    /// <see cref="LoginStepRow"/>, whose bridge signal already defaults its step to <c>LoginStep.Connecting</c>
    /// even while merely LoggedOut, which would incorrectly draw the first row as "current"/spinning before the
    /// flow has even started). Busy: the SAME four steps, now the real <see cref="LoginStepRow"/> off the live
    /// bridge signal, exactly like the takeover's own Finalizing splash.</summary>
    static Element ApproveCard(SetupSignInPhase facet, PlaybackBridge? bridge)
    {
        if (facet == SetupSignInPhase.Idle)
            return CardShell(
                new TextEl(Loc.Get(Strings.Setup.SignIn.AfterApprove)) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextPrimary },
                new BoxEl
                {
                    Direction = 0, Wrap = true, Gap = SetupLayout.RowGap * 2f,
                    Children =
                    [
                        PendingStepRow(Loc.Get(Strings.Auth.StepConnecting)),
                        PendingStepRow(Loc.Get(Strings.Auth.StepMetadata)),
                        PendingStepRow(Loc.Get(Strings.Auth.StepAudio)),
                        PendingStepRow(Loc.Get(Strings.Auth.StepProfile)),
                    ],
                });

        // Busy — bridge is guaranteed non-null here (DecisionColumn only calls this when it is).
        Element Row(LoginStep step, string label) => Embed.Comp(() => new LoginStepRow(bridge!.Login, step, label, ApproveRowWidth));
        return CardShell(
            new BoxEl { AlignSelf = FlexAlign.Start, Children = [Embed.Comp(() => new LoginStepBar(bridge!.Login))] },
            new BoxEl
            {
                Direction = 0, Wrap = true, Gap = SetupLayout.RowGap * 2f,
                Stagger = Motion.ReducedMotion ? 0f : WaveeMotion.StaggerMs,
                Children =
                [
                    Row(LoginStep.Connecting, Loc.Get(Strings.Auth.StepConnecting)),
                    Row(LoginStep.Metadata, Loc.Get(Strings.Auth.StepMetadata)),
                    Row(LoginStep.Audio, Loc.Get(Strings.Auth.StepAudio)),
                    Row(LoginStep.Profile, Loc.Get(Strings.Auth.StepProfile)),
                ],
            },
            new TextEl(Loc.Get(Strings.Setup.SignIn.BusyNote)) { Size = 11.5f, LineHeight = 17f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap });
    }

    static Element CardShell(params Element[] kids) => new BoxEl
    {
        Direction = 1, Gap = Spacing.S, Shrink = 0f, AlignSelf = FlexAlign.Stretch,
        Padding = Edges4.All(Spacing.M),
        Corners = Radii.CardAll, Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Children = kids,
    };

    /// <summary>One row of <see cref="ApproveCard"/>'s Idle-only pending preview — the same visual grammar
    /// <see cref="LoginStepRow"/>'s own "pending" branch uses (a dim bullet + tertiary label), but a plain static
    /// render: see this method's caller for why the live component can't be reused here.</summary>
    static Element PendingStepRow(string label) => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Width = ApproveRowWidth, Shrink = 0f, Height = 26f,
        Children =
        [
            new BoxEl
            {
                Width = 18f, Height = 18f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [new TextEl(Icons.RadioBullet) { Size = 11f, FontFamily = Theme.IconFont, Color = Tok.TextTertiary }],
            },
            new TextEl(label) { Size = 12f, LineHeight = 16f, Color = Tok.TextTertiary },
        ],
    };

    static Element TerminalInfoBar(SetupSignInPhase facet, string? error) => facet switch
    {
        SetupSignInPhase.Failed => FailedLeft(error),
        SetupSignInPhase.Expired => ExpiredLeft(),
        SetupSignInPhase.Premium => PremiumLeft(),
        _ => new BoxEl(),
    };

    // ── Non-Wide: the ORIGINAL "left pane / OR divider / compact QR pane" row, moved verbatim ─────────────────────────
    // No stage column to spend on the pairing pane below Wide, so this keeps today's layout completely unchanged —
    // a horizontal row at Compact, a vertical stack at Narrow/UltraNarrow (SetupLayout.StacksSignIn).

    static Element StackedOrRowBody(SetupSignInPhase facet, LoginSnapshot snap, PlaybackBridge? bridge, Action? startBrowser, SetupLayoutTier tier)
    {
        Element left = LeftPane(facet, snap, bridge, startBrowser) with
        {
            Key = "signin:" + facet,
            Enter = new EnterExit(Dy: 6f, Opacity: 0f, Active: true),
            Exit = new EnterExit(Dy: -4f, Opacity: 0f, Active: true),
            Transition = MotionTok.StandardEnter,
        };

        // The QR pane dims to ~22% while Busy (the prototype's `.login.busy`) and disappears once the flow has
        // moved past it (Done/Failed/Expired/Premium) — showing a live pairing code next to a result screen reads
        // as a second, contradicting affordance.
        float rightOpacity = SetupSignInPresentation.PaneOpacity(facet);
        bool rightInteractive = SetupSignInPresentation.PaneInteractive(facet);

        Element right = snap.Challenge is { } challenge
            ? LoginView.CompactRightPane(challenge)
            : PendingCodePane();
        right = right with
        {
            Key = snap.Challenge is { } c ? "signin:challenge:" + c.UserCode : "signin:challenge:pending",
            Enter = new EnterExit(Dy: 4f, Sx: 0.97f, Sy: 0.97f, Opacity: 0f, Active: true),
            Exit = new EnterExit(Dy: -2f, Opacity: 0f, Active: true),
            Transition = MotionTok.StandardEnter,
        };

        Element leftHost = new BoxEl
        {
            Grow = 1f, Basis = 0f, Shrink = 1f, MinWidth = 0f,
            Padding = new Edges4(Spacing.XXS, Spacing.XXS, Spacing.XL, Spacing.S),
            Children = [left],
        };
        Element divider = new BoxEl
        {
            Shrink = 0f, AlignSelf = FlexAlign.Stretch, Opacity = rightOpacity,
            HitTestVisible = rightInteractive,
            Children = [LoginView.OrDivider(SetupLayout.CompactDividerWidth, SetupLayout.StacksSignIn(tier))],
        };
        Element rightHost = new BoxEl
        {
            Shrink = 0f, AlignSelf = SetupLayout.StacksSignIn(tier) ? FlexAlign.Center : FlexAlign.Stretch,
            Opacity = rightOpacity, HitTestVisible = rightInteractive, Children = [right],
        };

        Element login = SetupLayout.StacksSignIn(tier)
            ? new BoxEl
            {
                Key = "signin:layout:" + (int)tier,
                Direction = 1, Gap = Spacing.M, MinWidth = 0f, MinHeight = 0f,
                Children = rightOpacity > 0f ? [leftHost, divider, rightHost] : [leftHost],
            }
            : new BoxEl
            {
                Key = "signin:layout:" + (int)tier,
                Direction = 0, AlignItems = FlexAlign.Start, MinWidth = 0f, MinHeight = 0f,
                Children = [leftHost, divider, rightHost],
            };

        // The content lane is intentionally taller than the row itself. Centering the login composition in it restores
        // the old takeover's balanced vertical rhythm while the surrounding page scroller still handles short windows.
        return new BoxEl
        {
            Direction = 1, MinWidth = 0f, MinHeight = SetupLayout.SignInBodyMinHeight,
            Justify = FlexJustify.Center,
            Children = [login],
        };
    }

    static Element LeftPane(SetupSignInPhase facet, LoginSnapshot snap, PlaybackBridge? bridge, Action? startBrowser) => facet switch
    {
        SetupSignInPhase.Idle => IdleLeft(startBrowser),
        SetupSignInPhase.Busy => BusyLeft(bridge),
        SetupSignInPhase.Done => DoneLeft(snap.User),
        SetupSignInPhase.Failed => FailedLeft(snap.Error),
        SetupSignInPhase.Expired => ExpiredLeft(),
        SetupSignInPhase.Premium => PremiumLeft(),
        _ => new BoxEl(),
    };

    // ── Idle: preserve the old takeover's identity and direct browser action inside the roomy left pane. ──
    static Element IdleLeft(Action? startBrowser) => new BoxEl
    {
        Direction = 1, Gap = Spacing.M,
        Children =
        [
            LoginView.SpotifyBrand(),
            SetupRows.Lead(Loc.Get(Strings.Auth.SpotifySignInWeb)),
            LoginView.BrowserLoginButton(startBrowser ?? Noop),
            new TextEl(Loc.Get(Strings.Auth.Disclaimer))
                { Size = 11.5f, LineHeight = 17f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap },
        ],
    };

    static void Noop() { }

    // ── Busy: the same step bar + four step rows the login takeover's own Finalizing splash uses, reading the SAME
    // bridge.Login signal — this page's own state, not a second dialog stacked on top of it. ─────────────────────
    static Element BusyLeft(PlaybackBridge? bridge)
    {
        if (bridge is null) return new BoxEl { Children = [new TextEl(Loc.Get(Strings.Auth.SigningIn)) { Size = 14f, Weight = 600, Color = Tok.TextPrimary }] };
        Element Row(LoginStep step, string label) => Embed.Comp(() => new LoginStepRow(bridge.Login, step, label));
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M,
            Children =
            [
                new TextEl(Loc.Get(Strings.Auth.SigningIn)) { Size = 14f, Weight = 600, Color = Tok.TextPrimary },
                new BoxEl { AlignSelf = FlexAlign.Start, Children = [Embed.Comp(() => new LoginStepBar(bridge.Login))] },
                new BoxEl
                {
                    Direction = 1, Gap = Spacing.XS, AlignSelf = FlexAlign.Stretch,
                    Stagger = Motion.ReducedMotion ? 0f : WaveeMotion.StaggerMs,
                    Children =
                    [
                        Row(LoginStep.Connecting, Loc.Get(Strings.Auth.StepConnecting)),
                        Row(LoginStep.Metadata, Loc.Get(Strings.Auth.StepMetadata)),
                        Row(LoginStep.Audio, Loc.Get(Strings.Auth.StepAudio)),
                        Row(LoginStep.Profile, Loc.Get(Strings.Auth.StepProfile)),
                    ],
                },
                new TextEl(Loc.Get(Strings.Setup.SignIn.BusyNote))
                    { Size = 11.5f, LineHeight = 17f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap },
            ],
        };
    }

    // ── Done: the "Is this you?" profile card (real avatar via PersonPicture, display name, email, Premium pill) + the
    // switch hint. The header carries the question and lead; the footer carries Yes / Not me. ────────────────────────
    // Reused by the Wide decision column too (DecisionColumn) — the identical card, not a second copy of it.
    static Element DoneLeft(WaveeUser? user)
    {
        var kids = new List<Element>
        {
            new TextEl(user?.DisplayName ?? "") { Size = 16f, LineHeight = 22f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
        };
        if (!string.IsNullOrWhiteSpace(user?.Email))
            kids.Add(new TextEl(user!.Email!) { Size = 12f, Color = Tok.TextSecondary });
        if (user?.IsPremium == true) kids.Add(PremiumPill());

        Element idCard = new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, Padding = Edges4.All(14f),
            Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardDefault,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                PersonPicture.Create(user?.AvatarUrl ?? "", 56f, displayName: user?.DisplayName ?? "") with { Shrink = 0f },
                new BoxEl { Direction = 1, Gap = 3f, MinWidth = 0f, Children = kids.ToArray() },
            ],
        };

        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M,
            Children = [idCard, SetupCompact.FinePrint(Loc.Get(Strings.Setup.SignIn.ConfirmHint), maxLines: 2)],
        };
    }

    static Element PremiumPill() => new BoxEl
    {
        Direction = 0, Gap = 5f, AlignItems = FlexAlign.Center, Height = 19f, Shrink = 0f,
        Padding = new Edges4(8f, 0f, 8f, 0f), Corners = CornerRadius4.All(9.5f),
        Fill = LoginView.SpotifyGreen with { A = 0.24f },
        Children = [new TextEl(Loc.Get(Strings.Auth.PremiumBadge)) { Size = 11f, Weight = 600, Color = LoginView.SpotifyGreen }],
    };

    // ── Failed / Expired / Premium: an InfoBar, reusing the exact same copy the login takeover shows. Reused by both
    // the non-Wide stacked layout (via LeftPane) and the Wide decision column (via TerminalInfoBar). ─────────────────
    static Element FailedLeft(string? error) => InfoBar.Create(
        InfoBarSeverity.Error,
        Loc.Get(Strings.Auth.CouldntSignIn),
        string.IsNullOrWhiteSpace(error) ? Loc.Get(Strings.Auth.NetworkError) : error!);

    static Element ExpiredLeft() => InfoBar.Create(
        InfoBarSeverity.Error, Loc.Get(Strings.Auth.CodeExpired), Loc.Get(Strings.Auth.CodeExpiredBody));

    static Element PremiumLeft() => InfoBar.Create(
        InfoBarSeverity.Error, Loc.Get(Strings.Auth.PremiumTitle), Loc.Get(Strings.Auth.PremiumBody));
}
