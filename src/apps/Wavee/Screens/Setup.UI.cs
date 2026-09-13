// ── Screens/Setup.UI.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the wizard (Terms / Sign in / Local playback), QrGrid, LoginView
//
// Role: UI
// Owner: R
// Wave: 6
// Budget: 1250 lines
// Spec: ch 28 §9.5
//
// Still owner R's Wave-6 wizard, EXCEPT the sign-in surface below, pulled forward by decision D2 (gap batch B5, G-030)
// so a fresh profile can sign in before Wave 6. Its pieces are the ones R's Sign in page composes later — the browser
// card, the pairing-code card, the QR grid — hosted for now in a standalone dialog the SIGN-IN DOOR opens:
//
//   Shell.OverlaysLayer (overlay host)
//   └ Setup.SignInDoor()            zero-size watcher: opens on a sign-in request or on entering SignInRequired
//       └ ContentDialog  460 DIP    "Sign in to Spotify"
//           ├ SignInBody            facet = Setup.SignInRules.Project(SignIn.State, Spotify.Status, Spotify.Fault)
//           │   ├ Busy:  lead · ring + step line
//           │   └ Idle / Failed / Expired / Premium:
//           │       ├ status block (Failed/Expired/Premium) or the lead sentence (Idle)
//           │       ├ SettingsCard  (globe) Continue in your browser  → SignIn.Start(Browser)   (PKCE loopback, primary)
//           │       ├ PairingCard   (camera) Scan the code with your phone  [QR]   (device grant, fallback; 1 Hz bound countdown)
//           │       └ "Wavee needs Spotify Premium.  Don't have a Spotify account? Sign up"
//           └ SignInFooter          one command row per facet
//
//   ┌─ Sign in to Spotify ─────────────────────────────────────────┐
//   │ Sign in with your Spotify® account on the Spotify® website — │
//   │ or scan the code with your phone.                            │
//   │ ┌──────────────────────────────────────────────────────────┐ │
//   │ │ (G) Continue in your browser                           › │ │
//   │ │    Opens accounts.spotify.com in your default browser.   │ │
//   │ └──────────────────────────────────────────────────────────┘ │
//   │ ┌──────────────────────────────────────────────────────────┐ │
//   │ │ (C) Scan the code with your phone             ┌───────┐  │ │
//   │ │    WZY5Q6TX · spotify.com/pair                │ # # # │  │ │
//   │ └───────────────────────────────────────────────└───────┘──┘ │
//   │ Expires in 09:41 · Copy code · Open spotify.com/pair         │
//   │ Wavee needs Spotify Premium.  Don't have an account? Sign up │
//   │                                          [ Close ] [Log in]  │
//   └──────────────────────────────────────────────────────────────┘
//
// Every decision is `Setup.SignInRules` (CORE, `Setup.cs`) and the flows are `Spotify.SignIn` (SHELL); this file is
// layout and wiring. The body copy helpers are the runtime card's (`+Setup.UI.Runtime.cs`), so both dialogs read alike.

using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

public static partial class Setup
{
    // ══ REGION — THE SIGN-IN SURFACE (gap batch B5, D2) ═════════════════════════════════════════════════════════════

    /// <summary>The sign-in door: a zero-size watcher that opens the sign-in surface when something asks for it
    /// (<see cref="Spotify.SignIn.Requests"/> — every "Sign in" with nothing to resume) or when the shell enters
    /// <c>SignInRequired</c> with no credential stored, and closes it once the session is online. Must be mounted INSIDE the
    /// overlay host, beside the runtime banner.</summary>
    // MOUNT POINT (gap batch B5 — Shell.OverlaysLayer)
    public static Element SignInDoor()
        => Platform.Args.Fake ? new BoxEl() : Embed.Comp(static () => new SignInDoorView());   // --fake never signs in: its scope is seeded, not an account

    /// <summary>Ask for the sign-in surface from anywhere on the UI thread — the same request <c>Spotify.Login()</c> makes
    /// when there is nothing to resume.</summary>
    public static void RequestSignIn() => Spotify.SignIn.Requests.Value = Spotify.SignIn.Requests.Peek() + 1;

    /// <summary>The open surface, or null. UI THREAD.</summary>
    static OverlayHandle? s_signIn;

    const string SpotifySignUpUrl = "https://www.spotify.com/signup";
    const string SpotifyPremiumUrl = "https://www.spotify.com/premium/";

    sealed class SignInDoorView : Component
    {
        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            int request = Spotify.SignIn.Requests.Value;
            Shell.AuthState auth = Shell.Auth.Value;
            Spotify.SessionPhase phase = Spotify.Status.Value;
            var lastRequest = UseRef(request);
            var lastAuth = UseRef<Shell.AuthState?>(null);

            UseEffect(() =>
            {
                bool requested = request != lastRequest.Value;
                lastRequest.Value = request;
                bool entered = auth == Shell.AuthState.SignInRequired && lastAuth.Value != Shell.AuthState.SignInRequired;
                lastAuth.Value = auth;
                if (SignInRules.OpensDoor(requested, entered, Platform.HasStoredCredential(), s_signIn is { IsOpen: true }))
                    OpenSignIn(overlay, requested ? "request" : "gate");
            }, DepKey.From(request, (int)auth));

            UseEffect(() =>
            {
                if (SignInRules.ClosesDoor(phase) && s_signIn is { IsOpen: true } open) open.Close();
            }, DepKey.From((int)phase));

            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        }
    }

    static void OpenSignIn(IOverlayService overlay, string door)
    {
        Log.Event(WaveeLogLevel.Info, "ui", "signin.open", "Sign-in surface opened", null, -1, null, WaveeLogField.Of("door", door));
        s_signIn = ContentDialog.Show(overlay, d =>
        {
            d.Title = Loc.Get(Strings.Setup.SignIn.Title);
            d.DialogWidth = SignInRules.DialogWidth;
            d.PrimaryText = "";                  // PrimaryText defaults to "OK": clear all three or a phantom OK appears
            d.SecondaryText = "";
            d.CloseText = "";
            d.Content = Embed.Comp(static () => new SignInBody());
            d.Footer = Embed.Comp(static () => new SignInFooter());
            d.Closed = static _ =>
            {
                s_signIn = null;
                Spotify.SignIn.Reset();
                Log.Event(WaveeLogLevel.Info, "ui", "signin.close", "Sign-in surface closed", null, -1, null,
                    WaveeLogField.Of("phase", Spotify.Status.Peek().ToString()));
            };
        });
    }

    static void CloseSignIn() => s_signIn?.Close();
    static void StartBrowser() => Spotify.SignIn.Start(Spotify.SignInMethod.Browser);
    static void StartPairing() => Spotify.SignIn.Start(Spotify.SignInMethod.DeviceCode);
    static void CancelBrowser() => Spotify.SignIn.Cancel(Spotify.SignInMethod.Browser);
    static void OpenExternal(string url) => InputHooks.Current.Default.OpenUri?.Invoke(url);

    // ── the body ────────────────────────────────────────────────────────────────────────────────────────────────────

    sealed class SignInBody : Component
    {
        public override Element Render()
        {
            Spotify.SignInState st = Spotify.SignIn.State.Value;
            Spotify.SessionPhase phase = Spotify.Status.Value;
            Spotify.SessionFault fault = Spotify.Fault.Value;
            SignInFacet facet = SignInRules.Project(in st, phase, fault);

            // The fallback is ready before anyone reaches for it: a pairing code is minted as the surface shows, never while
            // it is closed (a code minted early would lapse unseen). A lapsed or failed code waits for "Get a new code".
            bool needsCode = facet != SignInFacet.Done && SignInRules.NeedsPairingCode(in st);
            UseEffect(() => { if (needsCode) StartPairing(); }, DepKey.From(needsCode));

            Element body = facet switch
            {
                SignInFacet.Busy => Busy(in st, phase),
                SignInFacet.Done => RuntimeStatus(Icons.Accept, Tok.AccentDefault, Loc.Get(Strings.Auth.SigningIn), Loc.Get(Strings.Auth.StepProfile)),
                _ => Cards(facet, in st, fault),
            };
            return body with { Key = FacetKey(facet) };
        }

        static string FacetKey(SignInFacet facet) => facet switch
        {
            SignInFacet.Busy => "signin:busy",
            SignInFacet.Done => "signin:done",
            _ => "signin:cards",          // Idle and the three retry-in-place facets share one tree: the cards never remount
        };

        static Element Busy(in Spotify.SignInState st, Spotify.SessionPhase phase)
        {
            bool browser = !st.Handed;
            string lead = Loc.Get(browser ? Strings.Setup.SignIn.BrowserCardTitle : Strings.Auth.SigningIn);
            string step = Loc.Get(browser ? Strings.Auth.WaitingApproval
                : phase == Spotify.SessionPhase.Minting ? Strings.Auth.StepProfile : Strings.Auth.StepConnecting);
            return RuntimeColumn(
                RuntimeLead(lead),
                new BoxEl
                {
                    Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, MinHeight = 48f,
                    Children = [ProgressRing.Indeterminate(), RuntimeText(step, Tok.TextPrimary)],
                });
        }

        static Element Cards(SignInFacet facet, in Spotify.SignInState st, Spotify.SessionFault fault)
        {
            Element head = facet switch
            {
                SignInFacet.Failed => RuntimeStatus(Icons.StatusError, Tok.SystemFillCritical,
                    Loc.Get(Strings.Auth.CouldntSignIn), Loc.Get(SignInRules.FailureKey(in st, fault))),
                SignInFacet.Expired => RuntimeStatus(Icons.StatusWarning, Tok.SystemFillCaution,
                    Loc.Get(Strings.Auth.CodeExpired), Loc.Get(Strings.Auth.CodeExpiredBody)),
                SignInFacet.Premium => RuntimeStatus(Icons.StatusError, Tok.SystemFillCritical,
                    Loc.Get(Strings.Auth.PremiumTitle), Loc.Get(Strings.Auth.PremiumBody)),
                _ => RuntimeText(Loc.Get(Strings.Setup.SignIn.Lead)),
            };
            return new BoxEl
            {
                Direction = 1, Gap = Spacing.M,
                Children =
                [
                    head with { Key = "signin:head" },
                    SettingsCard.Create(new SettingsCard.Options
                    {
                        Header = Loc.Get(Strings.Setup.SignIn.BrowserCardTitle),
                        Description = Loc.Get(Strings.Setup.SignIn.BrowserCardSub),
                        HeaderIcon = Icons.Globe,
                        ActionIcon = Icons.OpenInNewWindow,
                        IsClickEnabled = true,
                        OnClick = StartBrowser,
                    }) with { Key = "signin:browser" },
                    Embed.Comp(static () => new PairingCard()) with { Key = "signin:pairing" },
                    PremiumRow() with { Key = "signin:premium" },
                ],
            };
        }

        static Element PremiumRow() => new BoxEl
        {
            Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinWidth = 0f,
            Children =
            [
                Caption(Loc.Get(Strings.Setup.SignIn.PremiumNote)) with { MinWidth = 0f },
                HyperlinkButton.Create(Loc.Get(Strings.Setup.SignIn.NoAccount), static () => OpenExternal(SpotifySignUpUrl),
                    size: ControlSize.Small),
            ],
        };
    }

    // ── the pairing-code card (its own component: the 1 Hz countdown is a BOUND text, never a card re-render) ──────────

    sealed class PairingCard : Component
    {
        public override Element Render()
        {
            Spotify.SignInState st = Spotify.SignIn.State.Value;
            var tick = UseSignal(0);
            UseInterval(() => tick.Value = tick.Peek() + 1, 1000f, enabled: st.HasChallenge);
            var hooks = UseContext(InputHooks.Current);

            if (!st.HasChallenge)
            {
                bool working = st.Code == Spotify.SignInStage.Starting || (st.Code == Spotify.SignInStage.Idle && !st.Handed);
                return SettingsCard.Create(new SettingsCard.Options
                {
                    Header = Loc.Get(Strings.Setup.SignIn.ScanCardTitleNeutral),
                    Description = working ? null
                        : Loc.Get(st.Code == Spotify.SignInStage.Failed ? SignInRules.LocGetCodeFailed : Strings.Auth.CodeExpiredBody),
                    HeaderIcon = Icons.Camera,
                    Content = working
                        ? new BoxEl
                        {
                            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
                            Children = [ProgressRing.Indeterminate(20f), Caption(Loc.Get(Strings.Auth.GettingCode))],
                        }
                        : Button.Standard(Loc.Get(Strings.Auth.GetNewCode), StartPairing),
                });
            }

            long expiresAt = st.CodeExpiresAtMs;
            string code = st.UserCode;
            string scanUri = st.VerificationUriComplete;
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
                        // Keyed by the code: a new code is a new symbol, and the grid's props freeze at mount.
                        Content = Embed.Comp(() => new QrGrid(scanUri, SignInRules.QrSize)) with { Key = "qr:" + code },
                    }),
                    new BoxEl
                    {
                        Direction = 0, Wrap = true, Gap = Spacing.XS, AlignItems = FlexAlign.Center, MinWidth = 0f,
                        Children =
                        [
                            new TextEl(Prop.Of(() =>
                            {
                                _ = tick.Value;           // the 1 Hz subscription: this bind re-runs, the card does not
                                return Strings.Auth.ExpiresIn(SignInRules.Countdown(expiresAt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
                            }))
                            {
                                Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary,
                            },
                            HyperlinkButton.Create(Loc.Get(Strings.Auth.CopyCode), () => hooks.Clipboard?.SetText(code), size: ControlSize.Small),
                            HyperlinkButton.Create(Loc.Get(SignInRules.LocOpenPairPage), () => OpenExternal(scanUri), size: ControlSize.Small),
                        ],
                    },
                ],
            };
        }
    }

    // ── the QR grid ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary><see cref="Qr.Encode"/>'s matrix as COALESCED runs (consecutive dark modules in a row are one box — a few
    /// hundred nodes, not version²) on a white quiet-zone plate. Pure black on white whatever the theme: contrast is what a
    /// phone camera binarizes on. Renders once per mount; mount it keyed by its text.</summary>
    sealed class QrGrid(string text, float size) : Component
    {
        static readonly ColorF Ink = ColorF.FromRgba(0x00, 0x00, 0x00);
        static readonly ColorF Paper = ColorF.FromRgba(0xFF, 0xFF, 0xFF);

        public override Element Render()
        {
            bool[,] m;
            try { m = Qr.Encode(text, Qr.Ecc.M); }
            catch (ArgumentException) { return new BoxEl { Width = size, Height = size, Fill = Paper, Corners = Radii.CardAll }; }

            int n = m.GetLength(0);
            int cell = QrPlate.CellFor(size, n);
            float plate = QrPlate.PlateFor(size, n);
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
                    spans.Add(new BoxEl { Width = (x - start) * cell, Height = cell, Fill = dark ? Ink : ColorF.Transparent });
                }
                rows[y] = new BoxEl { Direction = 0, Width = n * cell, Height = cell, Children = spans.ToArray() };
            }
            return new BoxEl
            {
                Width = plate, Height = plate, Shrink = 0f, Fill = Paper, Corners = Radii.CardAll, ClipToBounds = true,
                Children = [new BoxEl { Direction = 1, Width = plate, Height = plate, Padding = Edges4.All(4 * cell), Children = rows }],
            };
        }
    }

    // ── the footer ──────────────────────────────────────────────────────────────────────────────────────────────────

    sealed class SignInFooter : Component
    {
        const float BtnMinW = 96f, BtnH = 32f;

        public override Element Render()
        {
            Spotify.SignInState st = Spotify.SignIn.State.Value;
            SignInFacet facet = SignInRules.Project(in st, Spotify.Status.Value, Spotify.Fault.Value);
            return facet switch
            {
                SignInFacet.Busy => st.Handed
                    ? Row(null, Standard(Loc.Get(Strings.Auth.Close), CloseSignIn))
                    : Row(null, Standard(Loc.Get(Strings.Auth.Cancel), CancelBrowser)),
                SignInFacet.Done => Row(null, Accent(Loc.Get(Strings.Auth.Close), CloseSignIn)),
                SignInFacet.Premium => Row(
                    HyperlinkButton.Create(Loc.Get(Strings.Auth.Upgrade), static () => OpenExternal(SpotifyPremiumUrl)) with { Margin = new Edges4(-11f, 0f, 0f, 0f) },
                    Standard(Loc.Get(Strings.Auth.Close), CloseSignIn),
                    Accent(Loc.Get(Strings.Auth.UseAnotherAccount), StartBrowser)),
                SignInFacet.Expired => Row(null, Standard(Loc.Get(Strings.Auth.Close), CloseSignIn),
                    Accent(Loc.Get(Strings.Auth.GetNewCode), StartPairing)),
                SignInFacet.Failed => Row(null, Standard(Loc.Get(Strings.Auth.Close), CloseSignIn),
                    Accent(Loc.Get(Strings.Auth.TryAgain), RetryFor(in st))),
                _ => Row(null, Standard(Loc.Get(Strings.Auth.Close), CloseSignIn), Accent(Loc.Get(Strings.Auth.LogIn), StartBrowser)),
            };
        }

        /// <summary>"Try again" retries the flow that failed: the pairing code when only it failed, the browser otherwise
        /// (a failed login after a hand-off included — the browser is the primary way in).</summary>
        static Action RetryFor(in Spotify.SignInState st)
        {
            bool codeFailed = st.Code is Spotify.SignInStage.Failed or Spotify.SignInStage.Denied;
            bool browserFailed = st.Browser is Spotify.SignInStage.Failed or Spotify.SignInStage.Denied or Spotify.SignInStage.Expired;
            return codeFailed && !browserFailed ? StartPairing : StartBrowser;
        }

        static Element Standard(string text, Action onClick) =>
            Button.Standard(text, onClick) with { MinWidth = BtnMinW, Height = BtnH, MinHeight = BtnH, Justify = FlexJustify.Center };

        static Element Accent(string text, Action onClick) =>
            Button.Accent(text, onClick) with { MinWidth = BtnMinW, Height = BtnH, MinHeight = BtnH, Justify = FlexJustify.Center, TabIndex = 1 };

        static Element Row(Element? left, params Element[] right)
        {
            var kids = new List<Element>(right.Length + 2);
            if (left is not null) kids.Add(left);
            kids.Add(new BoxEl { Grow = 1f, HitTestVisible = false });
            kids.AddRange(right);
            return new BoxEl { Direction = 0, Grow = 1f, Gap = Spacing.S, AlignItems = FlexAlign.Center, Children = kids.ToArray() };
        }
    }

    // ══ END REGION (gap batch B5) ═══════════════════════════════════════════════════════════════════════════════════
}
