// ── Themes/Zune/Structure/Views/Screens/ZuneSetup.SignIn.cs — Zune's sign-in surfaces (themes plan 6.3, P5 Z8b) ─────────────────────────────────
//
// Board Z-Setup: TWO SURFACES, ONE SET OF SIGN-IN PARTS. (1) The wizard's Sign in page: the browser card (a title, its line, the round "go" button) beside the scan card (an 80 QR
// on white, "scan with your phone", the code and its address, "expires in 4:59 · copy code"), the waiting line under them, the Premium note. (2) The SIGN-IN DOOR: the body and the
// footer of the dialog the shell opens for every sign-in the wizard does not own; the same cards, stacked. Both compose the same browser card, pairing card and QR grid.
//
// The flows are core: the facet fold, the pairing code's mint, the verbs and the account lookup are `ISetupSignInHost` (the props carry it); this code reads its facts inside the
// render (so it subscribes exactly as the components that read the sign-in state did) and draws them.

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
    // ── the door ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The registered template of <see cref="ViewKeys.SetupSignInDoor"/>: the dialog's body or its footer.</summary>
    public static Element SignInDoorTemplate(SetupSignInDoorProps props, in ViewContext ctx)
    {
        var layout = ZuneSetupTiers.Of(ctx.Tier());   // the dialog opens inside the window's tier: its commands are the tier's height
        return props.Part == SetupDialogPart.Footer ? DoorFooter(props.Host, layout) : DoorBody(props.Host, ctx, layout);
    }

    // ── the shared option cards (the door's body and the wizard's Sign in page) ────────────────────────────────────────

    /// <summary>"Continue in your browser": a panel that is one button, with the round go button at its end.</summary>
    private static Element BrowserCard(ISetupSignInHost host)
        => SetupText.Card(Loc.Get(Strings.Setup.SignIn.BrowserCardTitle), Loc.Get(Strings.Setup.SignIn.BrowserCardSub), Icons.Globe,
            ZuneKit.Disc(Icons.OpenInNewWindow), host.StartBrowser);

    /// <summary>"Wavee needs Spotify Premium.  Don't have a Spotify account? Sign up": ONE wrapping row.</summary>
    private static Element PremiumRow(ISetupSignInHost host, ZuneSetupLayout layout) => new BoxEl
    {
        Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinWidth = 0f,
        Children = [SetupText.Secondary(Loc.Get(Strings.Setup.SignIn.PremiumNote)), KitOf(layout).Cmd(Loc.Get(Strings.Setup.SignIn.NoAccount), host.OpenSignUp)],
    };

    // ── the door's body ─────────────────────────────────────────────────────────────────────────────────────────────

    private static Element DoorBody(ISetupSignInHost host, in ViewContext ctx, ZuneSetupLayout layout)
    {
        SetupSignInFacts f = host.Facts();

        // The fallback is ready before anyone reaches for it: a pairing code is minted as the surface shows, never while it is closed (a code minted early would lapse unseen). A
        // lapsed or failed code waits for "Get a new code".
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
        Setup.SignInFacet.Busy => "zsignin:busy",
        Setup.SignInFacet.Done => "zsignin:done",
        _ => "zsignin:cards",          // Idle and the three retry-in-place facets share one tree: the cards never remount
    };

    private static Element DoorBusy(in SetupSignInFacts f)
    {
        bool browser = !f.Handed;
        string lead = Loc.Get(browser ? Strings.Setup.SignIn.BrowserCardTitle : Strings.Auth.SigningIn);
        string step = Loc.Get(browser ? Strings.Auth.WaitingApproval : f.Minting ? Strings.Auth.StepProfile : Strings.Auth.StepConnecting);
        return RuntimeColumn(
            RuntimeLead(lead),
            new BoxEl
            {
                Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinHeight = Z.BusyRowMinHeight,
                Children = [ProgressRing.Indeterminate(), RuntimeText(step, Tok.TextPrimary)],
            });
    }

    private static Element DoorCards(ISetupSignInHost host, in SetupSignInFacts f, ZuneSetupLayout layout)
    {
        Element head = f.Facet switch
        {
            Setup.SignInFacet.Failed => RuntimeStatus(Icons.StatusError, Tok.SystemFillCritical, Loc.Get(Strings.Auth.CouldntSignIn), Loc.Get(f.FailureKey)),
            Setup.SignInFacet.Expired => RuntimeStatus(Icons.StatusWarning, Tok.SystemFillCaution, Loc.Get(Strings.Auth.CodeExpired), Loc.Get(Strings.Auth.CodeExpiredBody)),
            Setup.SignInFacet.Premium => RuntimeStatus(Icons.StatusError, Tok.SystemFillCritical, Loc.Get(Strings.Auth.PremiumTitle), Loc.Get(Strings.Auth.PremiumBody)),
            _ => RuntimeText(Loc.Get(Strings.Setup.SignIn.Lead)),
        };
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M,
            Children =
            [
                head with { Key = "zsignin:head" },
                BrowserCard(host) with { Key = "zsignin:browser" },
                Embed.Comp(() => new PairingCard(host)) with { Key = "zsignin:pairing" },
                PremiumRow(host, layout) with { Key = "zsignin:premium" },
            ],
        };
    }

    // ── the pairing-code card (its own component: the 1 Hz countdown re-renders THIS card at most, never a page) ────────

    /// <param name="host">The sign-in flows.</param>
    /// <param name="wizard">The wizard's scan card: the QR is asked for at the record's size and the countdown rides beside the code. The door keeps the 96 QR and its bound
    /// countdown. A mount constant.</param>
    private sealed class PairingCard(ISetupSignInHost host, bool wizard = false) : CommonTierComponent
    {
        public override Element Render()
        {
            var layout = ZuneSetupTiers.Of(ReadTier());
            var k = KitOf(layout);
            SetupPairingFacts st = host.Pairing();
            var tick = UseSignal(0);
            UseInterval(() => tick.Value = tick.Peek() + 1, Z.PairingTickMs, enabled: st.HasChallenge);
            var hooks = UseContext(InputHooks.Current);

            if (!st.HasChallenge)
            {
                bool working = st.Working;
                return SetupText.Card(Loc.Get(Strings.Setup.SignIn.ScanCardTitleNeutral),
                    working ? null : Loc.Get(st.CodeFailed ? Setup.SignInRules.LocGetCodeFailed : Strings.Auth.CodeExpiredBody),
                    Icons.Camera,
                    working
                        ? new BoxEl
                        {
                            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                            Children = [ProgressRing.Indeterminate(Z.PairingRingSize), ZuneText.Hint(Loc.Get(Strings.Auth.GettingCode))],
                        }
                        : k.Cmd(Loc.Get(Strings.Auth.GetNewCode), host.StartPairing, accent: true));
            }

            long expiresAt = st.ExpiresAtMs;
            string code = st.UserCode;
            string scanUri = st.ScanUri;
            float qrSize = wizard ? layout.QrSize : Setup.SignInRules.QrSize;
            // Keyed by the code: a new code is a new symbol, and the grid's props freeze at mount.
            Element qr = Embed.Comp(new QrProps(scanUri, qrSize), static () => new QrGrid()) with { Key = "zqr:" + code };
            _ = tick.Value;   // the 1 Hz subscription: this small card re-renders, the page (and everything above it) does not
            string left = Strings.Auth.ExpiresIn(Setup.SignInRules.Countdown(expiresAt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            return new BoxEl
            {
                Direction = 0, Gap = Z.CardGap, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch, MinWidth = 0f,
                Padding = Edges4.All(Z.CardPad), Fill = Tok.FillSolidTertiary,
                Children =
                [
                    qr,
                    new BoxEl
                    {
                        Direction = 1, Gap = Z.CardWordsGap, Grow = 1f, Basis = 0f, MinWidth = 0f,
                        Children =
                        [
                            ZuneText.Label(Loc.Get(Strings.Setup.SignIn.ScanCardTitleNeutral)),
                            R.Type.DenseMeta(Strings.Setup.SignIn.PairLine(code)) with { Color = Tok.TextPrimary, MinWidth = 0f, Wrap = TextWrap.Wrap },
                            new BoxEl
                            {
                                Direction = 0, Wrap = true, Gap = Spacing.XS, AlignItems = FlexAlign.Center, MinWidth = 0f,
                                Children =
                                [
                                    ZuneText.Hint(left) with { Shrink = 0f },
                                    k.Cmd(Loc.Get(Strings.Auth.CopyCode), () => hooks.Clipboard?.SetText(code)),
                                    k.Cmd(Loc.Get(Setup.SignInRules.LocOpenPairPage), () => host.OpenExternal(scanUri)),
                                ],
                            },
                        ],
                    },
                ],
            };
        }
    }

    // ── the QR grid ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary><see cref="Setup.Qr.Encode"/>'s matrix as COALESCED runs (consecutive dark modules in a row are one box: a few hundred nodes, not version squared) on a white
    /// quiet-zone plate. Pure black on white whatever the theme: contrast is what a phone camera binarizes on. Renders once per mount; mount it keyed by its text. Unencodable
    /// text paints the same white plate with a music note.</summary>
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
                    Width = size, Height = size, Shrink = 0f, Fill = Z.QrPaper, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [new TextEl(Icons.MusicNote) { Size = Z.QrFallbackGlyphSize, FontFamily = Tok.Definition.Fonts.Icon, Color = Z.QrFallbackGlyph }],
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
                    spans.Add(new BoxEl { Width = (x - start) * cell, Height = cell, Fill = dark ? Z.QrInk : ColorF.Transparent });
                }
                rows[y] = new BoxEl { Direction = 0, Width = n * cell, Height = cell, Children = spans.ToArray() };
            }
            return new BoxEl
            {
                Width = plate, Height = plate, Shrink = 0f, Fill = Z.QrPaper, ClipToBounds = true,
                Children = [new BoxEl { Direction = 1, Width = plate, Height = plate, Padding = Edges4.All(4 * cell), Children = rows }],
            };
        }
    }

    // ── the door's footer ───────────────────────────────────────────────────────────────────────────────────────────

    private static Element DoorFooter(ISetupSignInHost host, ZuneSetupLayout layout)
    {
        SetupSignInFacts f = host.Facts();
        var k = KitOf(layout);
        Element Word(string text, Action onClick) => k.Cmd(text, onClick);
        Element Go(string text, Action onClick) => k.Go(text, onClick);
        return f.Facet switch
        {
            Setup.SignInFacet.Busy => f.Handed
                ? DoorRow(null, Word(Loc.Get(Strings.Auth.Close), host.CloseDoor))
                : DoorRow(null, Word(Loc.Get(Strings.Auth.Cancel), host.CancelBrowser)),
            Setup.SignInFacet.Done => DoorRow(null, Go(Loc.Get(Strings.Auth.Close), host.CloseDoor)),
            Setup.SignInFacet.Premium => DoorRow(
                Word(Loc.Get(Strings.Auth.Upgrade), host.OpenPremium),
                Word(Loc.Get(Strings.Auth.Close), host.CloseDoor),
                Go(Loc.Get(Strings.Auth.UseAnotherAccount), host.StartBrowser)),
            Setup.SignInFacet.Expired => DoorRow(null, Word(Loc.Get(Strings.Auth.Close), host.CloseDoor), Go(Loc.Get(Strings.Auth.GetNewCode), host.StartPairing)),
            Setup.SignInFacet.Failed => DoorRow(null, Word(Loc.Get(Strings.Auth.Close), host.CloseDoor), Go(Loc.Get(Strings.Auth.TryAgain), host.Retry)),
            _ => DoorRow(null, Word(Loc.Get(Strings.Auth.Close), host.CloseDoor), Go(Loc.Get(Strings.Auth.LogIn), host.StartBrowser)),
        };
    }

    private static Element DoorRow(Element? left, params Element[] right)
    {
        var kids = new List<Element>(right.Length + 2);
        if (left is not null) kids.Add(left);
        kids.Add(new BoxEl { Grow = 1f, HitTestVisible = false });
        kids.AddRange(right);
        return new BoxEl { Direction = 0, Grow = 1f, Gap = Z.FooterActionGap, AlignItems = FlexAlign.Center, Children = kids.ToArray() };
    }

    // ── page 1 · Sign in ────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly string[] FacetKeys = ["zsignin:Idle", "zsignin:Busy", "zsignin:Done", "zsignin:Failed", "zsignin:Expired", "zsignin:Premium"];

    /// <summary>The registered template of <see cref="ViewKeys.SetupSignIn"/>.</summary>
    public static Element SignInPageTemplate(SetupSignInPageProps props, in ViewContext ctx)
    {
        var session = props.Session;
        var host = props.Host;
        var layout = ZuneSetupTiers.Of(ctx.Tier());
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
        return WizardFrame(Setup.WizardPage.SignIn, header, body);
    }

    private static Element CardsBody(ISetupSignInHost host, Setup.SignInFacet facet, string failure, ZuneSetupLayout layout)
    {
        Element lead = facet switch
        {
            Setup.SignInFacet.Failed => ZuneKit.Notice(InfoBarSeverity.Error, Loc.Get(Strings.Auth.CouldntSignIn), failure),
            Setup.SignInFacet.Expired => ZuneKit.Notice(InfoBarSeverity.Error, Loc.Get(Strings.Auth.CodeExpired), Loc.Get(Strings.Auth.CodeExpiredBody)),
            Setup.SignInFacet.Premium => ZuneKit.Notice(InfoBarSeverity.Error, Loc.Get(Strings.Auth.PremiumTitle), Loc.Get(Strings.Auth.PremiumBody)),
            _ => SetupText.Body(Loc.Get(Strings.Setup.SignIn.Lead)),
        };
        Element browser = BrowserCard(host);
        Element scan = Embed.Comp(() => new PairingCard(host, wizard: true)) with { Key = "zsignin:scan" };
        // Where the plate has room the two cards share a row; Compact stacks them. ONE tree: the cards' box is a row that wraps, each card growing from its own basis.
        Element cards = new BoxEl
        {
            Key = "zsignin:cards", Direction = 0, Wrap = true, Gap = Z.CardsGap, AlignSelf = FlexAlign.Stretch, AlignItems = FlexAlign.Stretch, MinWidth = 0f,
            Children =
            [
                new BoxEl { Key = "zsignin:cell:browser", Direction = 1, Grow = 1f, Shrink = 1f, Basis = layout.CardBasis, MinWidth = 0f, Children = [browser] },
                new BoxEl { Key = "zsignin:cell:scan", Direction = 1, Grow = 1f, Shrink = 1f, Basis = layout.CardBasis, MinWidth = 0f, Children = [scan] },
            ],
        };
        return SetupText.Stack(lead, cards, PremiumRow(host, layout));
    }

    private static Element BusyBody(ISetupSignInHost host, in SetupSignInFacts f) => SetupText.Stack(
        SetupText.Lead(Loc.Get(Strings.Setup.SignIn.Waiting)),
        ZuneKit.Notice(InfoBarSeverity.Informational, Loc.Get(Strings.Auth.SigningIn), Loc.Get(f.BusyMessageKey)),
        new BoxEl { AlignSelf = FlexAlign.Start, Children = [Embed.Comp(() => new LoginStepBar(host))] },
        new BoxEl
        {
            Direction = 0, Wrap = true, Gap = Spacing.L, Stagger = Design.Reduced ? 0f : R.Motion.StaggerMs,
            Children = [StepRow(host, 0), StepRow(host, 1), StepRow(host, 2), StepRow(host, 3)],
        });

    private static Element StepRow(ISetupSignInHost host, int step) => Embed.Comp(() => new LoginStepRow(host, step)) with { Key = "zlogin-step:" + step };

    /// <summary>"Is this you?": a plain account row (no auto-advance). The name arrives with the account's user row; until then the account id stands in.</summary>
    private static Element DoneBody(ISetupSignInHost host, ZuneSetupLayout layout)
    {
        SetupAccountFacts me = host.Account();   // subscribes: the account's identity lands a tick after Online
        return SetupText.Stack(
            SetupText.Lead(Loc.Get(Strings.Setup.SignIn.ConfirmLead)),
            new BoxEl
            {
                MinHeight = Z.CardMinHeight, Padding = Edges4.All(Z.CardPad), Fill = Tok.FillSolidTertiary,
                Direction = 0, Gap = Spacing.L, AlignItems = FlexAlign.Center,
                Children =
                [
                    PersonPicture.Create("", Z.AccountPicture, displayName: me.Name, imageSourcePath: me.ImageUrl) with { Shrink = 0f },
                    new BoxEl
                    {
                        Direction = 1, Gap = Z.CardWordsGap, Grow = 1f, Basis = 0f, MinWidth = 0f,
                        Children =
                        [
                            R.Type.TrackTitle(me.Name) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                            ZuneText.Hint(Loc.Get(me.Premium ? Strings.Auth.PremiumBadge : Strings.Setup.SignIn.Free)),
                        ],
                    },
                    KitOf(layout).Cmd(Loc.Get(Strings.Setup.SignIn.NotMe), host.SwitchAccount),
                ],
            },
            SetupText.Secondary(Loc.Get(Strings.Setup.SignIn.ConfirmHint)));
    }

    /// <summary>One rung of the Busy ladder: a ring while current, an accent check when done, a tertiary bullet pending, a critical cross on failure.</summary>
    private sealed class LoginStepRow(ISetupSignInHost host, int step) : Component
    {
        public override Element Render()
        {
            SetupLoginStepFacts s = host.Step();
            int cur = s.Step;
            bool done = cur > step, current = cur == step, failed = current && s.Failed;
            Element mark = current && !failed
                ? ProgressRing.Indeterminate(Z.StepRingSize)
                : new TextEl(failed ? Icons.Cancel : done ? Icons.Accept : Icons.RadioBullet)
                {
                    Size = failed || done ? Z.StepGlyphSize : Z.StepBulletSize, FontFamily = Tok.Definition.Fonts.Icon,
                    Color = failed ? Tok.SystemFillCritical : done ? Tok.AccentDefault : Tok.TextTertiary,
                };
            mark = mark with
            {
                Key = failed ? "zlogin-step-mark:failed" : done ? "zlogin-step-mark:done" : current ? "zlogin-step-mark:current" : "zlogin-step-mark:pending",
                Enter = new EnterExit(Sx: Z.MarkEnterScale, Sy: Z.MarkEnterScale, Opacity: 0f, Active: true),
                Exit = new EnterExit(Sx: Z.MarkExitScale, Sy: Z.MarkExitScale, Opacity: 0f, Active: true),
                Transition = MotionTok.ControlNormal,
            };
            var word = R.Type.DenseMeta(Loc.Get(Setup.Commands.LoginStepKey(step)));
            return new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Height = Z.StepRowHeight,
                Basis = Z.StepRowWidth, MaxWidth = Z.StepRowWidth, MinWidth = 0f, Shrink = 1f,
                Enter = new EnterExit(Dx: Z.StepEnterDx, Opacity: 0f, Active: true), Transition = MotionTok.ControlNormal,
                Children =
                [
                    new BoxEl { Width = Z.StepMarkBox, Height = Z.StepMarkBox, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [mark] },
                    word with { Weight = current ? Z.StepCurrentWeight : Z.StepRestWeight, Color = current ? Tok.TextPrimary : done ? Tok.TextSecondary : Tok.TextTertiary },
                ],
            };
        }
    }

    /// <summary>The ladder's determinate bar, its fill reflow-eased between rungs; Error on a failed login.</summary>
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
            return ProgressBar.Determinate(s.Step / (float)Setup.Commands.LoginSteps, Z.StepBarWidth, s.Failed ? ProgressBarState.Error : ProgressBarState.Normal, FillEase);
        }
    }
}
