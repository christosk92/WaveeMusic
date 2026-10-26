// ── Themes/Classic/Structure/Views/Screens/ClassicSetup.SignIn.cs — Classic's sign-in surfaces (themes plan 6.1, T11c) ──────────
//
// From `Screens\Setup.UI.cs`: TWO SURFACES, ONE SET OF SIGN-IN PARTS. (1) The wizard's Sign in page (`Setup.SignIn`). (2) The SIGN-IN DOOR (`Setup.SignInDoor`): the body and the footer of the 460
// ContentDialog the shell opens for every sign-in the wizard does not own. Both compose the same browser card, pairing card and QR grid.
//
// The flows are core: the facet fold, the pairing code's mint, the verbs and the account lookup are `ISetupSignInHost` (the props carry it); this code reads its facts inside the render (so
// it subscribes exactly as the components that read the sign-in state did) and draws them.

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

namespace Wavee.Themes.Classic;

internal static partial class ClassicSetup
{
    // ── the door ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The registered template of <see cref="ViewKeys.SetupSignInDoor"/>: the dialog's body or its footer.</summary>
    public static Element SignInDoorTemplate(SetupSignInDoorProps props, in ViewContext ctx)
    {
        var layout = ClassicSetupTiers.Of(ctx.Tier());   // the dialog opens inside the window's tier: its buttons and links are the tier's height
        return props.Part == SetupDialogPart.Footer ? DoorFooter(props.Host, layout) : DoorBody(props.Host, ctx, layout);
    }

    // ── the shared option cards (the door's body and the wizard's Sign in page) ────────────────────────────────────────

    private static Element BrowserCard(ISetupSignInHost host, ClassicSetupLayout layout, bool wizard) => SettingsCard.Create(new SettingsCard.Options
    {
        Header = Loc.Get(Strings.Setup.SignIn.BrowserCardTitle),
        Description = Loc.Get(Strings.Setup.SignIn.BrowserCardSub),
        HeaderIcon = Icons.Globe,
        ActionIcon = Icons.OpenInNewWindow,
        IsClickEnabled = true,
        OnClick = host.StartBrowser,
        Style = wizard ? SideCardStyle(layout) : null,
    });

    /// <summary>A side-by-side card's style (the record's 132 floor and padding); null where the cards stack at the stock card.</summary>
    private static SettingsCard.Style? SideCardStyle(ClassicSetupLayout layout)
        => layout.SignInSideBySide
            ? SettingsCard.DefaultStyle with { MinHeight = layout.SignInCardMinHeight, Padding = Edges4.All(layout.SignInCardPadding) }
            : null;

    /// <summary>"Wavee needs Spotify Premium.  Don't have a Spotify account? Sign up" — ONE wrapping row. The wizard sets it at
    /// body 14 secondary with a medium (32-DIP lane) link; the 460 door keeps caption size.</summary>
    private static Element PremiumRow(ISetupSignInHost host, bool wizard, ClassicSetupLayout layout) => new BoxEl
    {
        Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinWidth = 0f,
        Children =
        [
            (wizard ? Body(Loc.Get(Strings.Setup.SignIn.PremiumNote)).Secondary() : Caption(Loc.Get(Strings.Setup.SignIn.PremiumNote))) with { MinWidth = 0f },
            CommonTouch.Link(Loc.Get(Strings.Setup.SignIn.NoAccount), host.OpenSignUp, layout.Target,
                wizard ? ControlSize.Medium : ControlSize.Small),
        ],
    };

    // ── the door's body ─────────────────────────────────────────────────────────────────────────────────────────────

    private static Element DoorBody(ISetupSignInHost host, in ViewContext ctx, ClassicSetupLayout layout)
    {
        SetupSignInFacts f = host.Facts();

        // The fallback is ready before anyone reaches for it: a pairing code is minted as the surface shows, never while
        // it is closed (a code minted early would lapse unseen). A lapsed or failed code waits for "Get a new code".
        bool needsCode = f.NeedsPairingCode;
        ctx.Hooks.UseEffect(() => { if (needsCode) host.StartPairing(); }, DepKey.From(needsCode));

        Element body = f.Facet switch
        {
            Setup.SignInFacet.Busy => DoorBusy(f),
            Setup.SignInFacet.Done => RuntimeStatus(Icons.Accept, Tok.AccentDefault, Loc.Get(Strings.Auth.SigningIn), Loc.Get(Strings.Auth.StepProfile)),
            _ => DoorCards(host, f, layout),
        };
        return body with { Key = FacetKey(f.Facet) };
    }

    private static string FacetKey(Setup.SignInFacet facet) => facet switch
    {
        Setup.SignInFacet.Busy => "signin:busy",
        Setup.SignInFacet.Done => "signin:done",
        _ => "signin:cards",          // Idle and the three retry-in-place facets share one tree: the cards never remount
    };

    private static Element DoorBusy(in SetupSignInFacts f)
    {
        bool browser = !f.Handed;
        string lead = Loc.Get(browser ? Strings.Setup.SignIn.BrowserCardTitle : Strings.Auth.SigningIn);
        string step = Loc.Get(browser ? Strings.Auth.WaitingApproval
            : f.Minting ? Strings.Auth.StepProfile : Strings.Auth.StepConnecting);
        return RuntimeColumn(
            RuntimeLead(lead),
            new BoxEl
            {
                Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinHeight = ClassicSetupRules.BusyRowMinHeight,
                Children = [ProgressRing.Indeterminate(), RuntimeText(step, Tok.TextPrimary)],
            });
    }

    private static Element DoorCards(ISetupSignInHost host, in SetupSignInFacts f, ClassicSetupLayout layout)
    {
        Element head = f.Facet switch
        {
            Setup.SignInFacet.Failed => RuntimeStatus(Icons.StatusError, Tok.SystemFillCritical,
                Loc.Get(Strings.Auth.CouldntSignIn), Loc.Get(f.FailureKey)),
            Setup.SignInFacet.Expired => RuntimeStatus(Icons.StatusWarning, Tok.SystemFillCaution,
                Loc.Get(Strings.Auth.CodeExpired), Loc.Get(Strings.Auth.CodeExpiredBody)),
            Setup.SignInFacet.Premium => RuntimeStatus(Icons.StatusError, Tok.SystemFillCritical,
                Loc.Get(Strings.Auth.PremiumTitle), Loc.Get(Strings.Auth.PremiumBody)),
            _ => RuntimeText(Loc.Get(Strings.Setup.SignIn.Lead)),
        };
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M,
            Children =
            [
                head with { Key = "signin:head" },
                BrowserCard(host, layout, wizard: false) with { Key = "signin:browser" },
                Embed.Comp(() => new PairingCard(host)) with { Key = "signin:pairing" },
                PremiumRow(host, wizard: false, layout) with { Key = "signin:premium" },
            ],
        };
    }

    // ── the pairing-code card (its own component: the 1 Hz countdown re-renders THIS card at most, never a page) ────────

    /// <param name="host">The sign-in flows.</param>
    /// <param name="wizard">The wizard's scan card (ch 28 W3): the countdown rides in the one-line description
    /// ("CODE · spotify.com/pair  ·  Expires in mm:ss", a 1 Hz re-render of this card only) and the QR is asked for at 80
    /// (paints 82). The door (false) keeps the 96 QR and its bound countdown / copy / open row. A mount constant.</param>
    private sealed class PairingCard(ISetupSignInHost host, bool wizard = false) : CommonTierComponent
    {
        public override Element Render()
        {
            var layout = ClassicSetupTiers.Of(ReadTier());
            SetupPairingFacts st = host.Pairing();
            var tick = UseSignal(0);
            UseInterval(() => tick.Value = tick.Peek() + 1, ClassicSetupRules.PairingTickMs, enabled: st.HasChallenge);
            var hooks = UseContext(InputHooks.Current);

            if (!st.HasChallenge)
            {
                bool working = st.Working;
                return SettingsCard.Create(new SettingsCard.Options
                {
                    Header = Loc.Get(Strings.Setup.SignIn.ScanCardTitleNeutral),
                    Description = working ? null
                        : Loc.Get(st.CodeFailed ? Setup.SignInRules.LocGetCodeFailed : Strings.Auth.CodeExpiredBody),
                    HeaderIcon = Icons.Camera,
                    Style = wizard ? SideCardStyle(layout) : null,
                    Content = working
                        ? new BoxEl
                        {
                            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                            Children = [ProgressRing.Indeterminate(ClassicSetupRules.PairingRingSize), R.Type.DenseMeta(Loc.Get(Strings.Auth.GettingCode)) with { Color = Tok.TextTertiary }],
                        }
                        : CommonTouch.Button(Loc.Get(Strings.Auth.GetNewCode), host.StartPairing, layout.Target),
                });
            }

            long expiresAt = st.ExpiresAtMs;
            string code = st.UserCode;
            string scanUri = st.ScanUri;
            // Keyed by the code: a new code is a new symbol, and the grid's props freeze at mount.
            float qrSize = wizard ? layout.QrSize : Setup.SignInRules.QrSize;
            Element qr = Embed.Comp(new QrProps(scanUri, qrSize), static () => new QrGrid()) with { Key = "qr:" + code };   // keyed by the code; the size rides the props, so a tier crossing re-renders it
            if (wizard)
            {
                _ = tick.Value;   // the 1 Hz subscription: this small card re-renders, the page (and its Lottie hero) does not
                return SettingsCard.Create(new SettingsCard.Options
                {
                    Header = Loc.Get(Strings.Setup.SignIn.ScanCardTitleNeutral),
                    Description = Strings.Setup.SignIn.PairLine(code) + "  ·  "
                        + Strings.Auth.ExpiresIn(Setup.SignInRules.Countdown(expiresAt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())),
                    HeaderIcon = Icons.Camera,
                    Content = qr,
                    Style = SideCardStyle(layout),
                });
            }
            return new BoxEl
            {
                Direction = 1, Gap = Spacing.S,
                Children =
                [
                    SettingsCard.Create(new SettingsCard.Options
                    {
                        Header = Loc.Get(Strings.Setup.SignIn.ScanCardTitleNeutral),
                        Description = Strings.Setup.SignIn.PairLine(code),
                        HeaderIcon = Icons.Camera,
                        Content = qr,
                    }),
                    new BoxEl
                    {
                        Direction = 0, Wrap = true, Gap = Spacing.XS, AlignItems = FlexAlign.Center, MinWidth = 0f,
                        Children =
                        [
                            new TextEl(Prop.Of(() =>
                            {
                                _ = tick.Value;           // the 1 Hz subscription: this bind re-runs, the card does not
                                return Strings.Auth.ExpiresIn(Setup.SignInRules.Countdown(expiresAt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                            }))
                            {
                                Size = ClassicSetupRules.CaptionSize, LineHeight = ClassicSetupRules.CaptionLine, Color = Tok.TextSecondary,
                            },
                            CommonTouch.Link(Loc.Get(Strings.Auth.CopyCode), () => hooks.Clipboard?.SetText(code), layout.Target, ControlSize.Small),
                            CommonTouch.Link(Loc.Get(Setup.SignInRules.LocOpenPairPage), () => host.OpenExternal(scanUri), layout.Target, ControlSize.Small),
                        ],
                    },
                ],
            };
        }
    }

    // ── the QR grid ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary><see cref="Setup.Qr.Encode"/>'s matrix as COALESCED runs (consecutive dark modules in a row are one box — a few
    /// hundred nodes, not version²) on a white quiet-zone plate. Pure black on white whatever the theme: contrast is what a
    /// phone camera binarizes on. Renders once per mount; mount it keyed by its text. Unencodable text paints the same white
    /// plate with a music note in Spotify green (ch 28 §4.2).</summary>
    private sealed record QrProps(string Text, float Size);

    private sealed class QrGrid : Component
    {
        public override Element Render()
        {
            var props = UseProps<QrProps>();
            string text = props.Text;
            float size = props.Size;
            bool[,] m;
            try { m = Setup.Qr.Encode(text, Setup.Qr.Ecc.M); }
            catch (ArgumentException)
            {
                return new BoxEl
                {
                    Width = size, Height = size, Fill = ClassicSetupRules.QrPaper, Corners = Radii.CardAll, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [new TextEl(Icons.MusicNote) { Size = ClassicSetupRules.QrFallbackGlyphSize, FontFamily = Tok.Definition.Fonts.Icon, Color = ClassicSetupRules.QrFallbackGlyph }],
                };
            }

            int n = m.GetLength(0);
            int cell = Setup.QrPlate.CellFor(size, n);
            float plate = Setup.QrPlate.PlateFor(size, n);
            var rows = new Element[n];
            var spans = new List<Element>(16);
            for (int y = 0; y < n; y++)
            {
                spans.Clear();
                int x = 0;
                while (x < n)
                {
                    bool dark = m[x, y];
                    int start = x;
                    while (x < n && m[x, y] == dark) x++;
                    spans.Add(new BoxEl { Width = (x - start) * cell, Height = cell, Fill = dark ? ClassicSetupRules.QrInk : ColorF.Transparent });
                }
                rows[y] = new BoxEl { Direction = 0, Width = n * cell, Height = cell, Children = spans.ToArray() };
            }
            return new BoxEl
            {
                Width = plate, Height = plate, Shrink = 0f, Fill = ClassicSetupRules.QrPaper, Corners = Radii.CardAll, ClipToBounds = true,
                Children = [new BoxEl { Direction = 1, Width = plate, Height = plate, Padding = Edges4.All(4 * cell), Children = rows }],
            };
        }
    }

    // ── the door's footer ───────────────────────────────────────────────────────────────────────────────────────────

    private static Element DoorFooter(ISetupSignInHost host, ClassicSetupLayout layout)
    {
        SetupSignInFacts f = host.Facts();
        Element Standard(string text, Action onClick) => DoorStandard(text, onClick, layout);
        Element Accent(string text, Action onClick) => DoorAccent(text, onClick, layout);
        return f.Facet switch
        {
            Setup.SignInFacet.Busy => f.Handed
                ? DoorRow(null, Standard(Loc.Get(Strings.Auth.Close), host.CloseDoor))
                : DoorRow(null, Standard(Loc.Get(Strings.Auth.Cancel), host.CancelBrowser)),
            Setup.SignInFacet.Done => DoorRow(null, Accent(Loc.Get(Strings.Auth.Close), host.CloseDoor)),
            Setup.SignInFacet.Premium => DoorRow(
                CommonTouch.Link(Loc.Get(Strings.Auth.Upgrade), host.OpenPremium, layout.Target) with { Margin = ClassicSetupRules.FlushLinkMargin },
                Standard(Loc.Get(Strings.Auth.Close), host.CloseDoor),
                Accent(Loc.Get(Strings.Auth.UseAnotherAccount), host.StartBrowser)),
            Setup.SignInFacet.Expired => DoorRow(null, Standard(Loc.Get(Strings.Auth.Close), host.CloseDoor),
                Accent(Loc.Get(Strings.Auth.GetNewCode), host.StartPairing)),
            Setup.SignInFacet.Failed => DoorRow(null, Standard(Loc.Get(Strings.Auth.Close), host.CloseDoor),
                Accent(Loc.Get(Strings.Auth.TryAgain), host.Retry)),
            _ => DoorRow(null, Standard(Loc.Get(Strings.Auth.Close), host.CloseDoor), Accent(Loc.Get(Strings.Auth.LogIn), host.StartBrowser)),
        };
    }

    private static Element DoorStandard(string text, Action onClick, ClassicSetupLayout layout) =>
        Button.Standard(text, onClick) with
        { MinWidth = ClassicSetupRules.DialogButtonMinWidth, Height = layout.ButtonHeight, MinHeight = layout.ButtonHeight, Justify = FlexJustify.Center };

    private static Element DoorAccent(string text, Action onClick, ClassicSetupLayout layout) =>
        Button.Accent(text, onClick) with
        { MinWidth = ClassicSetupRules.DialogButtonMinWidth, Height = layout.ButtonHeight, MinHeight = layout.ButtonHeight, Justify = FlexJustify.Center, TabIndex = 1 };

    private static Element DoorRow(Element? left, params Element[] right)
    {
        var kids = new List<Element>(right.Length + 2);
        if (left is not null) kids.Add(left.Value);
        kids.Add(new BoxEl { Grow = 1f, HitTestVisible = false });
        kids.AddRange(right);
        return new BoxEl { Direction = 0, Grow = 1f, Gap = Spacing.S, AlignItems = FlexAlign.Center, Children = kids.ToArray() };
    }

    // ── page 1 · Sign in (B5's option cards, in the chapter's column) ───────────────────────────────────────────────

    private static readonly string[] FacetKeys = ["signin:Idle", "signin:Busy", "signin:Done", "signin:Failed", "signin:Expired", "signin:Premium"];

    /// <summary>The registered template of <see cref="ViewKeys.SetupSignIn"/>.</summary>
    public static Element SignInPageTemplate(SetupSignInPageProps props, in ViewContext ctx)
    {
        var session = props.Session;
        var host = props.Host;
        var layout = ClassicSetupTiers.Of(ctx.Tier());
        SetupSignInFacts f = host.Facts();
        Setup.SignInFacet facet = f.Facet;

        // The code is minted as this page is ACTIVE, not before, so it cannot lapse while someone reads the terms.
        bool needsCode = host.NeedsPairingChallenge(session.Page.Value);
        ctx.Hooks.UseEffect(() => { if (needsCode) host.StartPairing(); }, DepKey.From(needsCode));

        // The UIA live region: the code SPELLED OUT (non-assertive), and the two terminal errors (assertive).
        string code = f.UserCode;
        string failure = facet == Setup.SignInFacet.Failed ? Loc.Get(f.FailureKey) : "";
        ctx.Hooks.UseEffect(() =>
        {
            if (!FluentGpu.Input.Announcer.IsAvailable) return;
            if (facet == Setup.SignInFacet.Failed) FluentGpu.Input.Announcer.Say(failure, assertive: true);
            else if (facet == Setup.SignInFacet.Expired) FluentGpu.Input.Announcer.Say(Loc.Get(Strings.Auth.CodeExpired), assertive: true);
            else if (code.Length > 0)
                FluentGpu.Input.Announcer.Say(Loc.Get(Strings.Auth.ScanToLogIn) + ". " + Loc.Get(Strings.Auth.OrGoTo) + " spotify.com/pair, "
                    + Loc.Get(Strings.Auth.EnterCodeColon) + " " + Setup.SignInPresentation.SpellCode(code));
        }, DepKey.From((int)facet, StringComparer.Ordinal.GetHashCode(code)));

        Element body = (facet == Setup.SignInFacet.Busy ? BusyBody(host, f)
            : Setup.SignInPresentation.ShowsIdleCards(facet) ? CardsBody(host, facet, failure, layout)
            : DoneBody(host, layout)) with
        {
            Key = FacetKeys[(int)facet], Enter = BodyEnter, Exit = BodyExit, Transition = MotionTok.StandardEnter,
        };
        string header = Loc.Get(facet == Setup.SignInFacet.Done ? Strings.Setup.SignIn.IsThisYou : Strings.Setup.SignIn.Title);
        return WizardFrame(Setup.WizardPage.SignIn, header, body, backAutoPadding: false);
    }

    private static Element CardsBody(ISetupSignInHost host, Setup.SignInFacet facet, string failure, ClassicSetupLayout layout)
    {
        Element lead = facet switch
        {
            Setup.SignInFacet.Failed => InfoBar.Create(InfoBarSeverity.Error, Loc.Get(Strings.Auth.CouldntSignIn), failure, isClosable: false),
            Setup.SignInFacet.Expired => InfoBar.Create(InfoBarSeverity.Error, Loc.Get(Strings.Auth.CodeExpired), Loc.Get(Strings.Auth.CodeExpiredBody), isClosable: false),
            Setup.SignInFacet.Premium => InfoBar.Create(InfoBarSeverity.Error, Loc.Get(Strings.Auth.PremiumTitle), Loc.Get(Strings.Auth.PremiumBody), isClosable: false),
            _ => SetupText.Lead(Loc.Get(Strings.Setup.SignIn.Lead)),
        };
        Element browser = BrowserCard(host, layout, wizard: true);
        Element scan = Embed.Comp(() => new PairingCard(host, wizard: true)) with { Key = "signin:scan" };
        // Small and Huge: the two cards share a row (the plate has no icon column to give up); Standard's 498-DIP column and Compact stack them.
        Element cards = layout.SignInSideBySide
            ? new BoxEl
            {
                Key = "signin:cards:row", Direction = 0, Gap = Spacing.M, AlignSelf = FlexAlign.Stretch, AlignItems = FlexAlign.Stretch, MinWidth = 0f,
                Children =
                [
                    new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [browser] },
                    new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [scan] },
                ],
            }
            : new BoxEl { Key = "signin:cards:column", Direction = 1, Gap = SetupGeometry.BodySpacing, MinWidth = 0f, Children = [browser, scan] };
        return SetupText.Stack(lead, cards, PremiumRow(host, wizard: true, layout));
    }

    private static Element BusyBody(ISetupSignInHost host, in SetupSignInFacts f) => SetupText.Stack(
        SetupText.Lead(Loc.Get(Strings.Setup.SignIn.Waiting)),
        InfoBar.Create(InfoBarSeverity.Informational, Loc.Get(Strings.Auth.SigningIn), Loc.Get(f.BusyMessageKey), isClosable: false),
        new BoxEl { AlignSelf = FlexAlign.Start, Children = [Embed.Comp(() => new LoginStepBar(host))] },
        new BoxEl
        {
            Direction = 0, Wrap = true, Gap = Spacing.L, Stagger = Design.Reduced ? 0f : R.Motion.StaggerMs,
            Children = [StepRow(host, 0), StepRow(host, 1), StepRow(host, 2), StepRow(host, 3)],
        });

    private static Element StepRow(ISetupSignInHost host, int step) => Embed.Comp(() => new LoginStepRow(host, step)) with { Key = "login-step:" + step };

    /// <summary>"Is this you?": a plain 68-DIP account row — no green pill, no auto-advance. The name arrives with the
    /// account's user row; until then the account id stands in (the host's <c>Account</c> answers it).</summary>
    private static Element DoneBody(ISetupSignInHost host, ClassicSetupLayout layout)
    {
        SetupAccountFacts me = host.Account();   // subscribes: the account's identity lands a tick after Online
        return SetupText.Stack(
            SetupText.Lead(Loc.Get(Strings.Setup.SignIn.ConfirmLead)),
            new BoxEl
            {
                MinHeight = SettingsCard.MinHeight, Padding = Edges4.All(SettingsCard.Padding),
                Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Corners = CornerRadius4.All(Radii.Control),
                Direction = 0, Gap = Spacing.L, AlignItems = FlexAlign.Center,
                Children =
                [
                    PersonPicture.Create("", ClassicSetupRules.AccountPicture, displayName: me.Name, imageSourcePath: me.ImageUrl) with { Shrink = 0f },
                    new BoxEl
                    {
                        Direction = 1, Gap = ClassicSetupRules.AccountTextGap, Grow = 1f, Basis = 0f, MinWidth = 0f,
                        Children =
                        [
                            BodyStrong(me.Name) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                            Caption(Loc.Get(me.Premium ? Strings.Auth.PremiumBadge : Strings.Setup.SignIn.Free)),
                        ],
                    },
                    CommonTouch.Link(Loc.Get(Strings.Setup.SignIn.NotMe), host.SwitchAccount, layout.Target, ControlSize.Small),
                ],
            },
            SetupText.Secondary(Loc.Get(Strings.Setup.SignIn.ConfirmHint)));
    }

    /// <summary>One rung of the Busy ladder: a 16 ring while current, an accent check when done, a tertiary bullet pending,
    /// a critical cross on failure — each mark keyed by its state so it scale-pops.</summary>
    private sealed class LoginStepRow(ISetupSignInHost host, int step) : Component
    {
        public override Element Render()
        {
            SetupLoginStepFacts s = host.Step();
            int cur = s.Step;
            bool done = cur > step, current = cur == step, failed = current && s.Failed;
            Element mark = current && !failed
                ? ProgressRing.Indeterminate(ClassicSetupRules.StepRingSize)
                : new TextEl(failed ? Icons.Cancel : done ? Icons.Accept : Icons.RadioBullet)
                {
                    Size = failed || done ? ClassicSetupRules.StepGlyphSize : ClassicSetupRules.StepBulletSize, FontFamily = Tok.Definition.Fonts.Icon,
                    Color = failed ? Tok.SystemFillCritical : done ? Tok.AccentDefault : Tok.TextTertiary,
                };
            mark = mark with
            {
                Key = failed ? "login-step-mark:failed" : done ? "login-step-mark:done" : current ? "login-step-mark:current" : "login-step-mark:pending",
                Enter = new EnterExit(Sx: 0.72f, Sy: 0.72f, Opacity: 0f, Active: true),
                Exit = new EnterExit(Sx: 0.88f, Sy: 0.88f, Opacity: 0f, Active: true),
                Transition = MotionTok.ControlNormal,
            };
            return new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Height = ClassicSetupRules.StepRowHeight,
                Basis = ClassicSetupRules.StepRowWidth, MaxWidth = ClassicSetupRules.StepRowWidth, MinWidth = 0f, Shrink = 1f,
                Enter = new EnterExit(Dx: -6f, Opacity: 0f, Active: true), Transition = MotionTok.ControlNormal,
                Children =
                [
                    new BoxEl { Width = ClassicSetupRules.StepMarkBox, Height = ClassicSetupRules.StepMarkBox, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [mark] },
                    new TextEl(Loc.Get(Setup.Commands.LoginStepKey(step)))
                    {
                        Size = ClassicSetupRules.CaptionSize, LineHeight = ClassicSetupRules.CaptionLine, Weight = current ? (ushort)600 : (ushort)400,
                        Color = current ? Tok.TextPrimary : done ? Tok.TextSecondary : Tok.TextTertiary,
                    },
                ],
            };
        }
    }

    /// <summary>The ladder's determinate bar (220), its fill reflow-eased between rungs; Error on a failed login.</summary>
    private sealed class LoginStepBar(ISetupSignInHost host) : Component
    {
        private static readonly TemplateParts FillEase = new()
        {
            [ProgressBar.PartFill] = b => b with
            {
                Layout = new LayoutTransition(TransitionChannels.Size, MotionTok.ControlNormal.ToDynamics(),
                    Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Width),
            },
        };

        public override Element Render()
        {
            SetupLoginStepFacts s = host.Step();
            return ProgressBar.Determinate(s.Step / (float)Setup.Commands.LoginSteps, ClassicSetupRules.StepBarWidth,
                s.Failed ? ProgressBarState.Error : ProgressBarState.Normal, FillEase);
        }
    }
}
