using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Backend.Audio;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Page 1 · Sign in — Rise's own <c>ConnectPage</c>: one content column, no stage/decision split. The truth
/// is <see cref="PlaybackBridge.Login"/> + <see cref="PlaybackBridge.Auth"/>, folded by the already-written pure
/// <see cref="SetupCommands.Project"/> into the six <see cref="SetupSignInPhase"/> facets this page switches on — the
/// footer reads the exact same two signals through the exact same projection (<see cref="SetupSession.BuildCtx"/>),
/// so neither ever drifts from the other. <see cref="SetupSignInPresentation.ShowsIdleCards"/> is the one pure fact
/// both this page and its own theory tests read for "which facets keep the two option cards visible".
///
/// <para>Idle shows two <c>SettingsCard</c>s (continue in the browser / scan a QR code — the 80-DIP QR sits in the
/// scan card's own <c>Content</c> slot) and one "needs Premium · Sign up" row, sized to FIT the reference plate's
/// 325-DIP body lane (<see cref="SetupLayout.SignInIdleBodyHeight"/>); should copy ever push it past that, the body
/// ScrollView's persistent rail (<see cref="SetupPageHost"/>) says so. Busy replaces them with an <c>InfoBar</c> + the takeover's own
/// <see cref="LoginStepBar"/>/<see cref="LoginStepRow"/> ladder. Done — "Is this you?" — is a real, user-clicked
/// confirmation page: a plain account row (avatar, name, Premium/Free caption, "Not me"), no green pill. Failed/
/// Expired/Premium show an error <c>InfoBar</c> OVER the same two Idle cards, so the user can retry in place.</para></summary>
sealed class SetupSignInPage : Component
{
    public override Element Render()
    {
        var bridge = UseContext(PlaybackBridge.Slot);
        var snap = bridge?.Login.Value ?? new LoginSnapshot(LoginPhase.LoggedOut);   // subscribe → re-render on phase change
        var auth = bridge?.Auth.Value ?? AuthStatus.LoggedOut;                       // subscribe → re-render on the auth flip
        var facet = SetupCommands.Project(snap.Phase, snap.Step, auth);
        var session = SetupSession.Current;

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

        var activePage = session?.Page.Value ?? SetupPage.SignIn;
        bool needsChallenge = SetupCommands.NeedsPairingChallenge(activePage, snap.Phase, snap.Challenge is not null);
        UseEffect(() =>
        {
            if (needsChallenge) session?.RestartCode?.Invoke();
        }, needsChallenge);

        string header = Loc.Get(facet == SetupSignInPhase.Done ? Strings.Setup.SignIn.IsThisYou : Strings.Setup.SignIn.Title);

        Element body = facet switch
        {
            SetupSignInPhase.Busy => BusyBody(bridge, snap.Phase),
            SetupSignInPhase.Done => DoneBody(bridge, snap.User, session),
            _ => IdleOrRetryBody(facet, snap, session?.StartBrowser),
        } with
        {
            Key = "signin:" + facet,
            Enter = new EnterExit(Dy: 6f, Opacity: 0f, Active: true),
            Exit = new EnterExit(Dy: -4f, Opacity: 0f, Active: true),
            Transition = MotionTok.StandardEnter,
        };

        return SetupPageHost.Frame(SetupPage.SignIn, header, body, backAutoPadding: false);
    }

    // ── Idle + the three "retry in place" terminal facets (SetupSignInPresentation.ShowsIdleCards) ────────────────
    static Element IdleOrRetryBody(SetupSignInPhase facet, LoginSnapshot snap, Action? startBrowser)
    {
        var kids = new List<Element>(6);
        if (facet == SetupSignInPhase.Idle)
            kids.Add(SetupText.Lead(Loc.Get(Strings.Setup.SignIn.Lead)));
        else
            kids.Add(TerminalBar(facet, snap.Error));

        kids.Add(BrowserCard(startBrowser));
        kids.Add(ScanCardSlot(snap));
        kids.Add(PremiumRow());
        return SetupText.Stack([.. kids]);
    }

    /// <summary>"Wavee needs Spotify Premium.  Don't have a Spotify account? Sign up" — ONE 32-DIP row (secondary text
    /// + the inline HyperlinkButton), not two stacked rows: the Idle body has to fit the 325-DIP reference lane
    /// (<see cref="SetupLayout.SignInIdleBodyHeight"/>), and this row is the 40 DIP that made the difference.
    /// <c>Wrap</c> lets it fold at the 320-DIP minimum plate instead of overflowing sideways.</summary>
    static Element PremiumRow() => new BoxEl
    {
        Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinWidth = 0f,
        Children =
        [
            Ui.Body(Loc.Get(Strings.Setup.SignIn.PremiumNote)).Secondary() with { MinWidth = 0f },
            HyperlinkButton.Create(Loc.Get(Strings.Setup.SignIn.NoAccount), () => LoginView.OpenUrl("https://www.spotify.com/signup")),
        ],
    };

    static Element TerminalBar(SetupSignInPhase facet, string? error) => facet switch
    {
        SetupSignInPhase.Failed => InfoBar.Create(InfoBarSeverity.Error, Loc.Get(Strings.Auth.CouldntSignIn),
            string.IsNullOrWhiteSpace(error) ? Loc.Get(Strings.Auth.NetworkError) : error!, isClosable: false),
        SetupSignInPhase.Expired => InfoBar.Create(InfoBarSeverity.Error, Loc.Get(Strings.Auth.CodeExpired),
            Loc.Get(Strings.Auth.CodeExpiredBody), isClosable: false),
        _ => InfoBar.Create(InfoBarSeverity.Error, Loc.Get(Strings.Auth.PremiumTitle), Loc.Get(Strings.Auth.PremiumBody), isClosable: false),
    };

    static Element BrowserCard(Action? startBrowser) => SetupText.Card(
        Loc.Get(Strings.Setup.SignIn.BrowserCardTitle), Loc.Get(Strings.Setup.SignIn.BrowserCardSub),
        Icons.Globe, onClick: startBrowser);

    /// <summary>The scan-QR card: a live <see cref="SetupScanCard"/> once a challenge exists, else a "getting your
    /// code" placeholder (the code is minted asynchronously the moment this page mounts, deliberately not until the
    /// user reaches it, so it cannot expire while they read Terms).</summary>
    static Element ScanCardSlot(LoginSnapshot snap) => snap.Challenge is { } c
        ? Embed.Comp(new SetupScanCard.Props(c.VerificationUriComplete ?? c.VerificationUri, c.UserCode, c.Expiry), () => new SetupScanCard())
            with { Key = "signin:scan:" + c.UserCode }
        : PendingScanCard() with { Key = "signin:scan:pending" };

    static Element PendingScanCard() => SettingsCard.Create(new SettingsCard.Options
    {
        Header = Loc.Get(Strings.Setup.SignIn.ScanCardTitleNeutral),
        HeaderIcon = Icons.Camera,
        Content = new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            Children =
            [
                ProgressRing.Indeterminate(20f),
                new TextEl(Loc.Get(Strings.Auth.GettingCode)) { Size = 12.5f, Color = Tok.TextTertiary },
            ],
        },
    });

    // ── Busy: the takeover's own step bar + four step rows, reading the SAME bridge.Login signal. ──────────────────
    static Element BusyBody(PlaybackBridge? bridge, LoginPhase phase)
    {
        string message = phase is LoginPhase.RequestingCode or LoginPhase.LoggedOut
            ? Loc.Get(Strings.Auth.GettingCode) : Loc.Get(Strings.Auth.WaitingApproval);

        var kids = new List<Element>(3)
        {
            SetupText.Lead(Loc.Get(Strings.Setup.SignIn.Waiting)),
            InfoBar.Create(InfoBarSeverity.Informational, Loc.Get(Strings.Auth.SigningIn), message, isClosable: false),
        };
        if (bridge is not null)
        {
            Element Row(LoginStep step, string label) => Embed.Comp(() => new LoginStepRow(bridge.Login, step, label, 300f));
            kids.Add(new BoxEl { AlignSelf = FlexAlign.Start, Children = [Embed.Comp(() => new LoginStepBar(bridge.Login))] });
            kids.Add(new BoxEl
            {
                Direction = 0, Wrap = true, Gap = Spacing.L,
                Stagger = Motion.ReducedMotion ? 0f : WaveeMotion.StaggerMs,
                Children =
                [
                    Row(LoginStep.Connecting, Loc.Get(Strings.Auth.StepConnecting)),
                    Row(LoginStep.Metadata, Loc.Get(Strings.Auth.StepMetadata)),
                    Row(LoginStep.Audio, Loc.Get(Strings.Auth.StepAudio)),
                    Row(LoginStep.Profile, Loc.Get(Strings.Auth.StepProfile)),
                ],
            });
        }
        return SetupText.Stack([.. kids]);
    }

    // ── Done: "Is this you?" — a plain SettingsCard-styled account row, no green pill. ───────────────────────────────
    static Element DoneBody(PlaybackBridge? bridge, WaveeUser? snapshotUser, SetupSession? session)
    {
        var liveUser = bridge?.User.Value;
        var user = liveUser ?? snapshotUser;
        string name = SetupSignInPresentation.DisplayNameFor(liveUser, snapshotUser) ?? user?.DisplayName ?? "";
        return SetupText.Stack(
            SetupText.Lead(Loc.Get(Strings.Setup.SignIn.ConfirmLead)),
            AccountRow(name, user?.AvatarUrl, user?.IsPremium == true, session?.SwitchAccount),
            SetupText.Secondary(Loc.Get(Strings.Setup.SignIn.ConfirmHint)));
    }

    static Element AccountRow(string name, string? avatarUrl, bool premium, Action? switchAccount) => new BoxEl
    {
        MinHeight = SettingsCard.MinHeight, Padding = Edges4.All(SettingsCard.Padding),
        Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Corners = CornerRadius4.All(Radii.Control),
        Direction = 0, Gap = Spacing.L, AlignItems = FlexAlign.Center,
        Children =
        [
            // imageSourcePath, not the URL passed as `initials` (the old SetupAccountCard's bug) — a real avatar
            // photo shows when one exists, initials-from-name otherwise.
            PersonPicture.Create("", 40f, displayName: name, imageSourcePath: avatarUrl) with { Shrink = 0f },
            new BoxEl
            {
                Direction = 1, Gap = 2f, Grow = 1f, Basis = 0f, MinWidth = 0f,
                Children =
                [
                    BodyStrong(name) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    Caption(Loc.Get(premium ? Strings.Auth.PremiumBadge : Strings.Setup.SignIn.Free)).Secondary(),
                ],
            },
            new BoxEl { Grow = 1f, HitTestVisible = false },
            HyperlinkButton.Create(Loc.Get(Strings.Setup.SignIn.NotMe), switchAccount ?? Noop, size: ControlSize.Small),
        ],
    };

    static void Noop() { }
}

/// <summary>The Idle scan-QR card, isolated as its OWN <see cref="Component"/> so the pairing-code countdown's 1 Hz
/// tick (WinUI's "expires in mm:ss") re-renders only this small card, never the whole <see cref="SetupSignInPage"/>
/// (which also hosts a mounted Lottie) — the run-once-render rule (a bound prop for a hot value, never a per-frame
/// whole-page re-render).</summary>
sealed class SetupScanCard : Component
{
    internal sealed record Props(string Uri, string Code, DateTimeOffset Expiry);

    public override Element Render()
    {
        var p = UseProps<Props>();
        var post = Context.UsePost();
        var tick = UseSignal(0);
        var ticker = UseAsyncCommand(cancelOnUnmount: true);
        UseEffect(() => ticker.Run(async ct =>
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                post(() => tick.Value++);   // marshal the 1 Hz write to the UI thread (the loop runs off-thread)
            }
        }), DepKey.Empty);
        _ = tick.Value;   // subscribe → re-render this card each second

        var remaining = p.Expiry - DateTimeOffset.UtcNow;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        string mmss = ((int)remaining.TotalMinutes).ToString("00") + ":" + remaining.Seconds.ToString("00");
        string description = Strings.Setup.SignIn.PairLine(p.Code) + "  ·  " + Strings.Auth.ExpiresIn(mmss);

        return SettingsCard.Create(new SettingsCard.Options
        {
            Header = Loc.Get(Strings.Setup.SignIn.ScanCardTitleNeutral),
            Description = description,
            HeaderIcon = Icons.Camera,
            Content = Embed.Comp(() => new QrGrid(p.Uri, SetupLayout.QrSize)),
        });
    }
}
