// ── Screens/Setup.UI.cs ────────────────────────────────────────────────────────────────────────────────────────────
// the wizard (Terms / Sign in / Local playback), QrGrid, LoginView
//
// Role: UI
// Owner: R
// Wave: 6
// Budget: 1250 lines
// Spec: ch 28 §9.5
//
// TWO SURFACES, ONE SET OF SIGN-IN PARTS. (1) The SETUP WIZARD (WP-6.R: G-192, G-095's wizard half, G-030's wizard
// chrome): the 762×490 hand-rolled Modal plate `Setup.WizardChrome()` opens on a pending (or terms-re-armed) install.
// (2) The SIGN-IN DOOR (gap batch B5, D2): a 460 ContentDialog for every sign-in the wizard does not own. Both compose the
// same browser card, pairing card and QR grid; while the wizard is armed-and-unshown or open it OWNS sign-in and the door
// stands down (`WizardRules.OwnsSignIn`).
//
//   Shell.OverlaysLayer (overlay host)    mount order: Setup.WizardChrome() → Feedback.Chrome() → ReleaseNotes.AfterUpdateChrome()
//   ├ Setup.WizardChrome()   0×0, once per launch → overlay.Open(Modal, FocusTrap, ScrimVisual = false) → WizardPlate
//   │   WizardPlate 762×490   FillSolidBase · r8 · 1px StrokeSurfaceDefault · Elevation.Dialog · Enter / Backspace
//   │   ├ content  FillLayerAlt, pad 24 → Flow.KeepAlive(page; 3 entries; Nav.RecipeFor(Dir.Peek()); no replay on activate)
//   │   │   └ TermsPage | SignInPage | LocalPlaybackPage → WizardFrame (props) = [192 Lottie hero] · Title · ScrollView(body)
//   │   ├ separator 1 StrokeCardDefault
//   │   ├ WizardFooter 80 = [210: step label + 162 bar] · PRIMARY (left) · SECONDARY (right)      ← Commands.ResolveFor
//   │   └ [Local playback] Back 30×30 over the content region's 24-DIP corner
//   └ Setup.SignInDoor()     0×0 → ContentDialog 460 "Sign in to Spotify" → SignInBody + SignInFooter
//
//   +- 24 -+---- 192 ----+ 24 +------------- content 498 --------------+ 24 +
//   |      |  (Lottie,   |    | Sign in to Spotify               28/36   |    |
//   |      |  recoloured |    | lead · (globe) browser card · (cam) scan |    |   the body scrolls; the rail
//   |      |  to accent) |    | card + QR 82 · premium row               |    |   rides in the 24 pad
//   +------+-------------+----+------------------------------------------+----+  1
//   | Step 1 of 2  =====......  |  [        Log in        ] [     Close      ] |  80
//   +------------------------------------------------------------------------+
//
// THE SHELL'S HALF: `Setup.Covering` (Dim while the plate is up) is painted by the shell's cover scrim (owner I's chrome, not
// this file); `Setup.Runtime.WizardCovering` is written here while the Local playback page is up. Every decision is CORE
// (`Setup.Gating`, `Setup.SignInRules`, and the wizard rules parked in `Setup.Host.cs` region 1); this file lays out and wires.

using FluentGpu;
using FluentGpu.Animation;
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
    /// <c>SignInRequired</c> with no credential stored, and closes it once the session is online. Stands down while the
    /// setup wizard owns sign-in. Must be mounted INSIDE the overlay host, beside the runtime banner.</summary>
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
                // WP-6.R: never a second sign-in surface beside the wizard's own Sign in page.
                if (!WizardRules.OwnsSignIn(Gating.IsPending(Platform.Settings), WizardOpen.Peek(), s_wizardShown)
                    && SignInRules.OpensDoor(requested, entered, Platform.HasStoredCredential(), s_signIn is { IsOpen: true }))
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

    /// <summary>"Try again" retries the flow that failed: the pairing code when only it failed, the browser otherwise
    /// (a failed login after a hand-off included — the browser is the primary way in).</summary>
    static Action RetryFor(in Spotify.SignInState st)
    {
        bool codeFailed = st.Code is Spotify.SignInStage.Failed or Spotify.SignInStage.Denied;
        bool browserFailed = st.Browser is Spotify.SignInStage.Failed or Spotify.SignInStage.Denied or Spotify.SignInStage.Expired;
        return codeFailed && !browserFailed ? StartPairing : StartBrowser;
    }

    // ── the shared option cards (the door's body and the wizard's Sign in page) ────────────────────────────────────────

    static Element BrowserCard() => SettingsCard.Create(new SettingsCard.Options
    {
        Header = Loc.Get(Strings.Setup.SignIn.BrowserCardTitle),
        Description = Loc.Get(Strings.Setup.SignIn.BrowserCardSub),
        HeaderIcon = Icons.Globe,
        ActionIcon = Icons.OpenInNewWindow,
        IsClickEnabled = true,
        OnClick = StartBrowser,
    });

    /// <summary>"Wavee needs Spotify Premium.  Don't have a Spotify account? Sign up" — ONE wrapping row. The wizard sets it at
    /// body 14 secondary with a medium (32-DIP lane) link, the budget's <c>LinkRowHeight</c>; the 460 door keeps caption size.</summary>
    static Element PremiumRow(bool wizard) => new BoxEl
    {
        Direction = 0, Wrap = true, AlignItems = FlexAlign.Center, Gap = Spacing.XS, MinWidth = 0f,
        Children =
        [
            (wizard ? Body(Loc.Get(Strings.Setup.SignIn.PremiumNote)).Secondary() : Caption(Loc.Get(Strings.Setup.SignIn.PremiumNote))) with { MinWidth = 0f },
            HyperlinkButton.Create(Loc.Get(Strings.Setup.SignIn.NoAccount), static () => OpenExternal(SpotifySignUpUrl),
                size: wizard ? ControlSize.Medium : ControlSize.Small),
        ],
    };

    // ── the door's body ─────────────────────────────────────────────────────────────────────────────────────────────

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
                    BrowserCard() with { Key = "signin:browser" },
                    Embed.Comp(static () => new PairingCard()) with { Key = "signin:pairing" },
                    PremiumRow(wizard: false) with { Key = "signin:premium" },
                ],
            };
        }
    }

    // ── the pairing-code card (its own component: the 1 Hz countdown re-renders THIS card at most, never a page) ────────

    /// <param name="wizard">The wizard's scan card (ch 28 W3): the countdown rides in the one-line description
    /// ("CODE · spotify.com/pair  ·  Expires in mm:ss", a 1 Hz re-render of this card only) and the QR is asked for at 80
    /// (paints 82). The door (false) keeps the 96 QR and its bound countdown / copy / open row. A mount constant.</param>
    sealed class PairingCard(bool wizard = false) : Component
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
                            Children = [ProgressRing.Indeterminate(20f), new TextEl(Loc.Get(Strings.Auth.GettingCode)) { Size = 12.5f, Color = Tok.TextTertiary }],
                        }
                        : Button.Standard(Loc.Get(Strings.Auth.GetNewCode), StartPairing),
                });
            }

            long expiresAt = st.CodeExpiresAtMs;
            string code = st.UserCode;
            string scanUri = st.VerificationUriComplete.Length > 0 ? st.VerificationUriComplete : st.VerificationUri;
            // Keyed by the code: a new code is a new symbol, and the grid's props freeze at mount.
            Element qr = Embed.Comp(() => new QrGrid(scanUri, wizard ? Layout.QrSize : SignInRules.QrSize)) with { Key = "qr:" + code };
            if (wizard)
            {
                _ = tick.Value;   // the 1 Hz subscription: this small card re-renders, the page (and its Lottie hero) does not
                return SettingsCard.Create(new SettingsCard.Options
                {
                    Header = Loc.Get(Strings.Setup.SignIn.ScanCardTitleNeutral),
                    Description = Strings.Setup.SignIn.PairLine(code) + "  ·  "
                        + Strings.Auth.ExpiresIn(SignInRules.Countdown(expiresAt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())),
                    HeaderIcon = Icons.Camera,
                    Content = qr,
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
    /// phone camera binarizes on. Renders once per mount; mount it keyed by its text. Unencodable text paints the same white
    /// plate with a music note in Spotify green (ch 28 §4.2).</summary>
    sealed class QrGrid(string text, float size) : Component
    {
        static readonly ColorF Ink = ColorF.FromRgba(0x00, 0x00, 0x00);
        static readonly ColorF Paper = ColorF.FromRgba(0xFF, 0xFF, 0xFF);
        static readonly ColorF FallbackGlyph = ColorF.FromRgba(0x1D, 0xB9, 0x54);

        public override Element Render()
        {
            bool[,] m;
            try { m = Qr.Encode(text, Qr.Ecc.M); }
            catch (ArgumentException)
            {
                return new BoxEl
                {
                    Width = size, Height = size, Fill = Paper, Corners = Radii.CardAll, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [new TextEl(Icons.MusicNote) { Size = 28f, FontFamily = Theme.IconFont, Color = FallbackGlyph }],
                };
            }

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

    // ── the door's footer ───────────────────────────────────────────────────────────────────────────────────────────

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

    // ══ REGION — THE SETUP WIZARD (WP-6.R: G-192, G-095's wizard half, G-030's wizard chrome) ═══════════════════════

    // ── the contract (owner R) ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Bumped at the end of EVERY wizard close (complete, decline, Escape). The report chrome and the after-update
    /// chrome subscribe to re-evaluate their wizard deferral in the same launch.</summary>
    public static readonly Signal<int> WizardMarkerEpoch = new(0);

    /// <summary>True while the wizard plate is live (from open until its close has finished).</summary>
    public static readonly Signal<bool> WizardOpen = new(false);

    /// <summary>What the shell paints behind the plate: <see cref="Cover.Dim"/> while it is up. The plate opens with the
    /// engine scrim OFF (a shell is always behind it in 0.3), so the shell's cover scrim — Tok.FillSmoke, 250 ms linear,
    /// 0 under reduced motion — is the only dim.</summary>
    public static readonly Signal<Cover> Covering = new(Cover.None);

    static OverlayHandle? s_wizard;
    static bool s_wizardShown;

    /// <summary>Called once by <c>Settings.InstallScreens()</c>. The wizard needs no seam filled; when it is due this launch
    /// the three Lottie heroes are parsed and compiled off the UI thread before the first page mounts one.</summary>
    public static void InstallWizard()
    {
        var settings = Platform.Settings;
        bool due = WizardRules.ShouldOpen(Gating.IsPending(settings), Gating.IsCompleted(settings), settings.Get(Platform.Keys.TermsAcceptedVersion));
        if (due) WarmHeroes();
        Log.Info("setup", due ? "setup wizard installed — due this launch" : "setup wizard installed — not due");
    }

    /// <summary>Zero-size chrome, mounted in the overlay layer FIRST (before the report and after-update chromes). Once per
    /// launch it opens the plate when <c>setup.pending</c> is armed or a terms re-arm is due.</summary>
    // MOUNT POINT (owner R contract — Shell.OverlaysLayer, first of the three chromes)
    public static Element WizardChrome() => Embed.Comp(static () => new WizardChromeView());

    sealed class WizardChromeView : Component
    {
        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            var post = UsePost();
            UseEffect(() =>
            {
                if (s_wizardShown) return;
                var settings = Platform.Settings;
                bool completed = Gating.IsCompleted(settings);
                if (!WizardRules.ShouldOpen(Gating.IsPending(settings), completed, settings.Get(Platform.Keys.TermsAcceptedVersion))) return;
                bool signedIn = Platform.HasStoredCredential();
                var entry = WizardRules.EntryFor(completed, signedIn);
                WarmHeroes();
                void Open() { if (!s_wizardShown) OpenWizard(overlay, new WizardSession(entry, signedIn, post)); }
                if (WizardRules.OpenPosts(entry) == 2) post(() => post(Open));
                else post(Open);
            }, DepKey.Empty);
            return new BoxEl { Width = 0f, Height = 0f, HitTestVisible = false };
        }
    }

    /// <summary>THE open and THE close funnel. A programmatic close (the session's own "Open Wavee", "Decline" on a re-arm,
    /// a finish) always goes through; a user dismissal only on an idle terms re-arm. Every close lands in ClosedAction.</summary>
    static void OpenWizard(IOverlayService overlay, WizardSession session)
    {
        s_wizardShown = true;
        Log.Info("setup", "setup wizard opened (" + session.Entry + ", on " + session.Page.Peek() + ")");
        OverlayHandle handle = overlay.Open(
            static () => NodeHandle.Null,
            () => Embed.Comp(() => new WizardPlate(session)),
            FlyoutPlacement.BottomCenter,
            new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.Modal, Chrome: PopupChrome.Modal) { ScrimVisual = false });
        handle.ClosingAction = cause =>
            cause == OverlayCloseCause.Programmatic || WizardRules.EscapeClosesPlate(false, session.Entry, session.IsBusy);
        handle.ClosedAction = () =>
        {
            if (ReferenceEquals(s_wizard, handle)) s_wizard = null;
            session.End();
            Covering.Value = Cover.None;
            Runtime.WizardCovering.SetIfChanged(false);
            WizardOpen.Value = false;
            WizardMarkerEpoch.Value = WizardMarkerEpoch.Peek() + 1;
            Log.Info("setup", "setup wizard closed (pending " + Gating.IsPending(Platform.Settings) + ")");
        };
        session.RequestClose = handle.Close;
        s_wizard = handle;
        Covering.Value = Layout.CoverFor(shellBehind: true);
        WizardOpen.Value = true;
    }

    // ── the live session (outside the tree; the pages and the footer read its signals) ─────────────────────────────

    /// <summary>The wizard's live model: the page and the swap direction as signals (Dir is written BEFORE Page and only
    /// Peeked by the keep-alive, so a motion-only write never re-activates a page), and the Primary / Secondary / Back verbs
    /// every footer row and key maps to (ch 28 §6.1's two tables).</summary>
    sealed class WizardSession
    {
        public readonly Signal<WizardPage> Page;
        public readonly Signal<Design.NavTransitionKind> Dir = new(Design.NavTransitionKind.Neutral);
        public readonly WizardEntry Entry;
        public readonly bool SkipSignIn;
        public Action? RequestClose;
        readonly Action<Action> _post;

        /// <summary>The runtime provisioning model, begun when the wizard first reaches Local playback; null while no
        /// host is installed (D3), which is the page's model-null arm.</summary>
        public RuntimeModel? Model { get; private set; }

        public WizardSession(WizardEntry entry, bool signedIn, Action<Action> post)
        {
            Entry = entry;
            SkipSignIn = Gating.SkipSignIn(signedIn);
            _post = post;
            Page = new(WizardRules.StartPage(entry));
        }

        public bool IsBusy => Model?.IsBusy ?? false;

        /// <summary>The footer row. Reads the page, the sign-in facet and the runtime phase — subscribing when rendered.</summary>
        public CommandRow Row()
        {
            Spotify.SignInState st = Spotify.SignIn.State.Value;
            var ctx = new WizardCtx(Page.Value, SignInRules.Project(in st, Spotify.Status.Value, Spotify.Fault.Value),
                Model is { } m ? Commands.FacetFor(m.Phase.Value) : RuntimeFacet.Offer);
            return Commands.ResolveFor(in ctx, Model is not null);
        }

        public void Advance(WizardPage to)
        {
            if (to == WizardPage.LocalPlayback && Model is null && Runtime.Host is not null)
                Model = Runtime.Begin(_post, onClose: Finish, onWizardExit: () => { Gating.MarkDeferred(Platform.Settings); RequestClose?.Invoke(); });
            Dir.Value = WizardRules.DirectionFor(Page.Peek(), to);
            Page.Value = to;
        }

        public void Back() => Advance(Gating.PrevPage(Page.Peek(), SkipSignIn));

        /// <summary>Ends the runtime session and the sign-in flows (ClosedAction).</summary>
        public void End()
        {
            if (Model is { } m) Runtime.End(m);
            Model = null;
            Spotify.SignIn.Reset();
        }

        void Finish()
        {
            Gating.MarkCompleted(Platform.Settings);
            RequestClose?.Invoke();
        }

        static void QuitApp()
        {
            Log.Info("setup", "setup wizard: quit from a page with nothing behind it");
            Tray.Host.Quit();
        }

        public void Primary()
        {
            switch (Page.Peek())
            {
                case WizardPage.Terms:
                    // A consent leaves a durable record BEFORE the advance, so a crash in between cannot lose it.
                    Platform.Settings.Set(Platform.Keys.TermsAcceptedVersion, Gating.TermsVersion);
                    if (Entry == WizardEntry.TermsRearm) Finish();
                    else Advance(Gating.NextPage(WizardPage.Terms, SkipSignIn));
                    break;
                case WizardPage.SignIn:
                    Spotify.SignInState st = Spotify.SignIn.State.Peek();
                    switch (SignInRules.Project(in st, Spotify.Status.Peek(), Spotify.Fault.Peek()))
                    {
                        case SignInFacet.Idle: StartBrowser(); break;
                        case SignInFacet.Done:   // "Is this you?" — moving on is the user's own click, never automatic
                            if (Gating.SkipsLocalPlayback(Entry, Runtime.Status.Peek().IsReady)) Finish();
                            else Advance(WizardPage.LocalPlayback);
                            break;
                        case SignInFacet.Failed: RetryFor(in st)(); break;
                        case SignInFacet.Expired: StartPairing(); break;
                        case SignInFacet.Premium: OpenExternal(SpotifyPremiumUrl); break;
                    }
                    break;
                default:
                    if (Model is not { } m) return;   // no host: the row offers the primary disabled
                    switch (m.Phase.Peek())
                    {
                        case RuntimePhase.Offer: m.StartDownload(); break;
                        case RuntimePhase.Advanced: m.InstallSelected(); break;
                        case RuntimePhase.Untrusted: m.ConfirmUntrusted(); break;
                        case RuntimePhase.Ready: m.Close(); break;   // OnClose = Finish: the last page has no Done page after it
                        case RuntimePhase.Failed: m.Retry(); break;
                    }
                    break;
            }
        }

        public void Secondary()
        {
            switch (Page.Peek())
            {
                case WizardPage.Terms:
                    // Pre-auth there is nothing behind a first run's plate: Decline quits and the marker stays armed. A re-arm
                    // has a live shell behind it and just closes.
                    if (Entry == WizardEntry.TermsRearm) RequestClose?.Invoke();
                    else QuitApp();
                    break;
                case WizardPage.SignIn:
                    Spotify.SignInState st = Spotify.SignIn.State.Peek();
                    switch (SignInRules.Project(in st, Spotify.Status.Peek(), Spotify.Fault.Peek()))
                    {
                        case SignInFacet.Busy:   // "Cancel" stops the sign-in, never Wavee
                            if (st.Handed) { Spotify.SignIn.Reset(); Spotify.Logout(); }
                            else CancelBrowser();
                            break;
                        case SignInFacet.Premium: Spotify.SignIn.Reset(); StartPairing(); break;   // "Use a different account"
                        case SignInFacet.Done: SwitchAccount(); break;                           // "Not me"
                        default: QuitApp(); break;                                               // Idle / Failed / Expired "Close"
                    }
                    break;
                default:
                    if (Model is not { } m || m.Phase.Peek() is RuntimePhase.Offer or RuntimePhase.Failed)
                    {
                        // "Not now": a settled "no" — the banner stays quiet, and the wizard finishes outright.
                        Runtime.Dismiss("setup");
                        Log.Info("setup", "setup wizard: local playback declined");
                        Finish();
                        return;
                    }
                    switch (m.Phase.Peek())
                    {
                        case RuntimePhase.Advanced: m.Back(); break;                 // a MODEL step, not a page walk
                        case RuntimePhase.Untrusted: m.CancelUntrusted(); break;
                        case RuntimePhase.FetchingCatalog or RuntimePhase.Downloading: m.Cancel(); break;
                    }
                    break;
            }
        }
    }

    static void SwitchAccount()
    {
        Log.Info("setup", "setup wizard: not me — signing this PC out");
        Spotify.SignIn.Reset();
        Spotify.Logout();
    }

    // ── the plate ───────────────────────────────────────────────────────────────────────────────────────────────────

    sealed class WizardPlate(WizardSession session) : Component
    {
        public override Element Render()
        {
            Size2 vp = UseContextSignal(Viewport.Size).Value;
            WizardPage page = session.Page.Value;
            UseEffect(() => { Runtime.WizardCovering.SetIfChanged(page == WizardPage.LocalPlayback); }, DepKey.From((int)page));

            void OnKey(KeyEventArgs e)
            {
                if (e.Handled) return;
                if (e.KeyCode == Keys.Enter && session.Row().PrimaryEnabled) { session.Primary(); e.Handled = true; }
                else if (e.KeyCode == Keys.Back && Gating.ShowsBack(session.Page.Peek())) { session.Back(); e.Handled = true; }
            }

            // PagesHost claims Grow/Shrink/Min 0 so the keep-alive (a ComponentEl with no layout columns) gets a DEFINITE box
            // and the page's ScrollView scrolls instead of shrink-wrapping and clipping (ch 28 §9.1 #14).
            Element pages = new BoxEl
            {
                Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true,
                Children =
                [
                    Flow.KeepAlive(() => session.Page.Value, static p => "setup:page:" + (int)p, p => PageSlot(session, p),
                        new KeepAliveOptions(MaxEntries: 3, TransitionFor: (_, _) => Design.Nav.RecipeFor(session.Dir.Peek()),
                            SuppressLayoutTransitionsOnActivation: true)),
                ],
            };
            Element chrome = new BoxEl
            {
                Key = "setup:chrome", Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f,
                Children =
                [
                    new BoxEl
                    {
                        Grow = 1f, Shrink = 1f, MinHeight = 0f, Fill = Tok.FillLayerAlt, Padding = Edges4.All(Layout.PlatePadding),
                        Children = [pages],
                    },
                    new BoxEl { Height = Layout.SeparatorHeight, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeCardDefault },
                    Embed.Comp(() => new WizardFooter(session)),
                ],
            };
            return new BoxEl
            {
                ZStack = true,
                Width = Layout.Width(vp.Width), Height = Layout.Height(vp.Height), MinWidth = Layout.MinPlateWidth, MinHeight = Layout.MinPlateHeight,
                Corners = Radii.OverlayAll, Fill = Tok.FillSolidBase, BorderWidth = 1f, BorderColor = Tok.StrokeSurfaceDefault,
                Shadow = Elevation.Dialog, ClipToBounds = true, OnKeyDown = OnKey,
                Children = Gating.ShowsBack(page) ? new[] { chrome, BackOverlay() } : new[] { chrome },
            };
        }

        // Over the content region's own 24-DIP corner, so it reads as part of the header row.
        Element BackOverlay() => new BoxEl
        {
            Key = "setup:back", Direction = 0, AlignItems = FlexAlign.Start, Justify = FlexJustify.Start,
            Padding = Edges4.All(Layout.PlatePadding), HitTestPassThrough = true,
            Children =
            [
                IconButton.Create(Icons.Back, session.Back,
                    style: IconButton.DefaultStyle with { Size = Layout.BackButtonSize, GlyphSize = Layout.BackGlyphSize }),
            ],
        };
    }

    static Element PageSlot(WizardSession session, WizardPage page) => new BoxEl
    {
        Key = "setup:capture:" + (int)page, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
        Children =
        [
            page switch
            {
                WizardPage.Terms => Embed.Comp(() => new TermsPage(session)) with { Key = "setup:page:terms" },
                WizardPage.SignIn => Embed.Comp(() => new SignInPage(session)) with { Key = "setup:page:sign-in" },
                _ => Embed.Comp(() => new LocalPlaybackPage(session)) with { Key = "setup:page:local-playback" },
            },
        ],
    };

    // ── the footer (Rise's ControlGrid: primary LEFT, secondary RIGHT) ──────────────────────────────────────────────

    sealed class WizardFooter(WizardSession session) : Component
    {
        const float ButtonH = 32f;

        public override Element Render()
        {
            bool large = Layout.ShowsIcon(UseContextSignal(Viewport.Size).Value.Width);
            WizardPage page = session.Page.Value;
            CommandRow row = session.Row();

            var kids = new List<Element>(3);
            if (large)
            {
                var step = Gating.StepNumber(page);
                kids.Add(new BoxEl
                {
                    // Pinned to the button lane; the 48 is a RIGHT pad (Edges4 is L, T, R, B — the shipped footer-band bug).
                    Width = Layout.ProgressColumnFor(true), Height = ButtonH, Shrink = 0f, Direction = 1,
                    Padding = new Edges4(0f, 0f, Layout.ProgressColumnRightPad, 0f), Gap = Layout.ProgressStackGap,
                    Justify = FlexJustify.Center, AlignItems = FlexAlign.Start, AlignSelf = FlexAlign.Center,
                    Children =
                    [
                        new TextEl(step is { } n ? Strings.Setup.StepOf(n.Step, n.Total) : Loc.Get(Strings.Setup.PreSetup))
                        {
                            Size = 14f, Weight = 600, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                        ProgressBar.Determinate(Gating.Progress(page), Layout.ProgressWidth),
                    ],
                });
            }
            string primary = Loc.Get(row.PrimaryKey ?? Strings.Setup.Accept);
            kids.Add(Stretch(row.PrimaryKind == ButtonKind.Standard
                ? Button.Standard(primary, session.Primary, isEnabled: row.PrimaryEnabled)
                : Button.Accent(primary, session.Primary, isEnabled: row.PrimaryEnabled)));
            // A null secondary (Verifying, Ready) lets the primary span the whole action lane.
            if (row.SecondaryKey is { } secondary)
                kids.Add(Stretch(Button.Standard(Loc.Get(secondary), session.Secondary, isEnabled: row.SecondaryEnabled)));

            return new BoxEl
            {
                Height = Layout.FooterHeight, Shrink = 0f, Padding = Edges4.All(Layout.FooterPadding), Fill = Tok.FillSolidBase,
                Direction = 0, Gap = Layout.FooterColumnGap, AlignItems = FlexAlign.Center, Children = kids.ToArray(),
            };
        }

        static BoxEl Stretch(BoxEl button) => button with
        {
            Grow = 1f, Basis = 0f, MinWidth = 0f, Shrink = 1f, Height = ButtonH, MinHeight = ButtonH, Justify = FlexJustify.Center,
        };
    }

    // ── the page frame (icon column + title + scrolling body) ───────────────────────────────────────────────────────

    /// <summary>A page's frame. The body is rebuilt whenever a page-local signal moves, so it rides RE-PUSHED props (a ctor
    /// arg would freeze the first tree). The outer box claims the parent's Grow/Shrink/Min so the ScrollView is bounded.</summary>
    static Element WizardFrame(WizardPage page, string header, Element body, bool backAutoPadding = true) => new BoxEl
    {
        Key = "setup:frame:" + (int)page, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
        Children = [Embed.Comp(new FrameProps(page, header, body, backAutoPadding), static () => new PageFrame())],
    };

    sealed record FrameProps(WizardPage Page, string Header, Element Body, bool BackAutoPadding);

    sealed class PageFrame : Component
    {
        public override Element Render()
        {
            var p = UseProps<FrameProps>();
            bool iconShown = Layout.ShowsIcon(UseContextSignal(Viewport.Size).Value.Width);

            Element title = Title(p.Header) with { Grow = 1f, Basis = 0f, MinWidth = 0f, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.WordEllipsis };
            Element header = new BoxEl
            {
                Direction = 0, Shrink = 0f, Margin = new Edges4(0f, -Layout.HeaderTopPull, 0f, Layout.HeaderBottomGap),
                Children = p.BackAutoPadding && Layout.BackSpacerApplies(p.Page, iconShown)
                    ? new[] { new BoxEl { Width = Layout.BackSpacerWidth, Shrink = 0f }, title }
                    : new[] { title },
            };
            Element scroller = ScrollView(p.Body) with
            {
                Grow = 1f, Shrink = 1f, MinHeight = 0f, MinWidth = 0f,
                // The rail rides in the plate's own 24-DIP pad; AlwaysShowScrollbar so an overflowing lane SAYS it scrolls.
                Margin = new Edges4(0f, 0f, -Layout.ScrollGutter, 0f), Padding = new Edges4(0f, 0f, Layout.ScrollGutter, 0f),
                EdgeCues = ScrollEdgeCues.None, AlwaysShowScrollbar = true,
            };
            Element content = new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Children = [header, scroller] };
            if (!iconShown)
                return new BoxEl { Key = "setup:layout:compact", Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Children = [content] };

            FluentGpu.Lottie.LottieSource? source = s_heroes[(int)p.Page].Value;
            return new BoxEl
            {
                Key = "setup:layout:wide", Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Gap = Layout.IconColumnGap,
                Children =
                [
                    new BoxEl
                    {
                        Width = Layout.IconColumnWidth, Shrink = 0f, AlignSelf = FlexAlign.Stretch, Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
                        Children = source is null ? Array.Empty<Element>() : new[] { LottieView.Create(source, Layout.IconColumnWidth, HeroOptions) },
                    },
                    content,
                ],
            };
        }
    }

    // ── the Lottie heroes ───────────────────────────────────────────────────────────────────────────────────────────

    static readonly Lazy<FluentGpu.Lottie.LottieSource?>[] s_heroes =
    [
        new(static () => LoadHero(WizardPage.Terms)),
        new(static () => LoadHero(WizardPage.SignIn)),
        new(static () => LoadHero(WizardPage.LocalPlayback)),
    ];

    /// <summary>Rise's cadence (the first half of the timeline, once, then hold) recoloured to the live accent, zoomed 1.2×.
    /// Reduced motion stated explicitly: the static pose at To (never an author-side branch).</summary>
    static readonly LottieOptions HeroOptions = LottieOptions.RiseSetup with
    {
        Recolor = RecolorLive, Zoom = Layout.HeroZoom, ReducedMotion = ReducedMotionPolicy.SnapEnd,
    };

    /// <summary>The live seam over <see cref="Recolor.Apply"/>: today's accent tokens, read once per hero mount.</summary>
    static ColorF RecolorLive(ColorF c) => Recolor.Apply(c, Tok.AccentDefault, Tok.AccentTextPrimary);

    static FluentGpu.Lottie.LottieSource? LoadHero(WizardPage page)
    {
        string name = Layout.HeroAsset(page);
        try { return FluentGpu.Lottie.LottieSource.FromFile(Path.Combine(AppContext.BaseDirectory, "assets", "lottie", name + ".json")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("setup", "wizard hero '" + name + "' is unavailable — the icon column stays empty", ex);
            return null;
        }
    }

    /// <summary>Parse + compile the three heroes off the UI thread, so no page mount pays for it (a nicety: a cold hero
    /// compiles inline on first use).</summary>
    static void WarmHeroes() => _ = Task.Run(static () =>
    {
        foreach (var hero in s_heroes)
        {
            try { if (hero.Value is { } source) _ = source.Plan; }
            catch (Exception ex) { Log.Warn("setup", "wizard hero warm-up failed", ex); }
        }
    });

    // ── the page body vocabulary: five primitives and a pass-through card ───────────────────────────────────────────

    static class SetupText
    {
        public static Element Stack(params Element[] kids) => new BoxEl { Direction = 1, Gap = Layout.BodySpacing, MinWidth = 0f, Children = kids };
        public static Element Group(params Element[] kids) => new BoxEl { Direction = 1, Gap = Layout.BodyInnerSpacing, MinWidth = 0f, Children = kids };
        public static Element Lead(string s) => Ui.BodyStrong(s) with { Wrap = TextWrap.Wrap, MinWidth = 0f };
        public static Element Body(string s) => Ui.Body(s) with { Wrap = TextWrap.Wrap, MinWidth = 0f };
        public static Element Secondary(string s) => Ui.Body(s).Secondary() with { Wrap = TextWrap.Wrap, MinWidth = 0f };

        /// <summary>The engine SettingsCard as-is, so both of its responsive thresholds (content wrap, icon drop) survive.</summary>
        public static Element Card(string header, string? description, string? glyph = null, Element? content = null) =>
            SettingsCard.Create(new SettingsCard.Options { Header = header, Description = description, HeaderIcon = glyph, Content = content });
    }

    static readonly EnterExit BodyEnter = new(Dy: 6f, Opacity: 0f, Active: true);
    static readonly EnterExit BodyExit = new(Dy: -4f, Opacity: 0f, Active: true);

    // ── page 0 · Terms ──────────────────────────────────────────────────────────────────────────────────────────────

    sealed class TermsPage(WizardSession session) : Component
    {
        const string PrivacyUrl = "https://github.com/christosk92/WaveeMusic/blob/main/PRIVACY.md";

        public override Element Render()
        {
            Element body = SetupText.Stack(
                SetupText.Lead(Loc.Get(Strings.Setup.Terms.Start)),
                SetupText.Body(Loc.Get(Strings.Setup.Terms.LastUpdated)),
                SetupText.Body(Loc.Get(Strings.Setup.Welcome.Lead)),
                SetupText.Lead(Loc.Get(Strings.Setup.Terms.Section1Title)), SetupText.Body(Loc.Get(Strings.Setup.Terms.Section1Body)),
                SetupText.Lead(Loc.Get(Strings.Setup.Terms.Section2Title)), SetupText.Body(Loc.Get(Strings.Setup.Terms.Section2Body)),
                SetupText.Lead(Loc.Get(Strings.Setup.Terms.Section3Title)), SetupText.Body(Loc.Get(Strings.Setup.Terms.Section3Body)),
                SetupText.Lead(Loc.Get(Strings.Setup.Terms.Section4Title)), SetupText.Body(Loc.Get(Strings.Setup.Terms.Section4Body)),
                SetupText.Group(
                    SetupText.Secondary(Loc.Get(Strings.Setup.Terms.Fine)),
                    HyperlinkButton.Create(Loc.Get(Strings.Setup.Terms.PrivacyLink), static () => OpenExternal(PrivacyUrl), size: ControlSize.Small)));
            return WizardFrame(WizardPage.Terms, Loc.Get(WizardRules.TermsHeaderKey(session.Entry)), body, backAutoPadding: false);
        }
    }

    // ── page 1 · Sign in (B5's option cards, in the chapter's column) ───────────────────────────────────────────────

    sealed class SignInPage(WizardSession session) : Component
    {
        static readonly string[] FacetKeys = ["signin:Idle", "signin:Busy", "signin:Done", "signin:Failed", "signin:Expired", "signin:Premium"];

        public override Element Render()
        {
            Spotify.SignInState st = Spotify.SignIn.State.Value;
            Spotify.SessionFault fault = Spotify.Fault.Value;
            SignInFacet facet = SignInRules.Project(in st, Spotify.Status.Value, fault);

            // The code is minted as this page is ACTIVE, not before, so it cannot lapse while someone reads the terms.
            bool needsCode = Commands.NeedsPairingChallenge(session.Page.Value, facet, in st);
            UseEffect(() => { if (needsCode) StartPairing(); }, DepKey.From(needsCode));

            // The UIA live region: the code SPELLED OUT (non-assertive), and the two terminal errors (assertive).
            string code = st.HasChallenge ? st.UserCode : "";
            string failure = facet == SignInFacet.Failed ? Loc.Get(SignInRules.FailureKey(in st, fault)) : "";
            UseEffect(() =>
            {
                if (!FluentGpu.Input.Announcer.IsAvailable) return;
                if (facet == SignInFacet.Failed) FluentGpu.Input.Announcer.Say(failure, assertive: true);
                else if (facet == SignInFacet.Expired) FluentGpu.Input.Announcer.Say(Loc.Get(Strings.Auth.CodeExpired), assertive: true);
                else if (code.Length > 0)
                    FluentGpu.Input.Announcer.Say(Loc.Get(Strings.Auth.ScanToLogIn) + ". " + Loc.Get(Strings.Auth.OrGoTo) + " spotify.com/pair, "
                        + Loc.Get(Strings.Auth.EnterCodeColon) + " " + SignInPresentation.SpellCode(code));
            }, DepKey.From((int)facet, StringComparer.Ordinal.GetHashCode(code)));

            Element body = (facet == SignInFacet.Busy ? BusyBody(in st)
                : SignInPresentation.ShowsIdleCards(facet) ? CardsBody(facet, failure)
                : DoneBody()) with
            {
                Key = FacetKeys[(int)facet], Enter = BodyEnter, Exit = BodyExit, Transition = MotionTok.StandardEnter,
            };
            string header = Loc.Get(facet == SignInFacet.Done ? Strings.Setup.SignIn.IsThisYou : Strings.Setup.SignIn.Title);
            return WizardFrame(WizardPage.SignIn, header, body, backAutoPadding: false);
        }

        static Element CardsBody(SignInFacet facet, string failure) => SetupText.Stack(
            facet switch
            {
                SignInFacet.Failed => InfoBar.Create(InfoBarSeverity.Error, Loc.Get(Strings.Auth.CouldntSignIn), failure, isClosable: false),
                SignInFacet.Expired => InfoBar.Create(InfoBarSeverity.Error, Loc.Get(Strings.Auth.CodeExpired), Loc.Get(Strings.Auth.CodeExpiredBody), isClosable: false),
                SignInFacet.Premium => InfoBar.Create(InfoBarSeverity.Error, Loc.Get(Strings.Auth.PremiumTitle), Loc.Get(Strings.Auth.PremiumBody), isClosable: false),
                _ => SetupText.Lead(Loc.Get(Strings.Setup.SignIn.Lead)),
            },
            BrowserCard(),
            Embed.Comp(static () => new PairingCard(wizard: true)) with { Key = "signin:scan" },
            PremiumRow(wizard: true));

        static Element BusyBody(in Spotify.SignInState st) => SetupText.Stack(
            SetupText.Lead(Loc.Get(Strings.Setup.SignIn.Waiting)),
            InfoBar.Create(InfoBarSeverity.Informational, Loc.Get(Strings.Auth.SigningIn), Loc.Get(Commands.BusyMessageKey(in st)), isClosable: false),
            new BoxEl { AlignSelf = FlexAlign.Start, Children = [Embed.Comp(static () => new LoginStepBar())] },
            new BoxEl
            {
                Direction = 0, Wrap = true, Gap = Spacing.L, Stagger = Design.Reduced ? 0f : Design.Motion.StaggerMs,
                Children = [StepRow(0), StepRow(1), StepRow(2), StepRow(3)],
            });

        static Element StepRow(int step) => Embed.Comp(() => new LoginStepRow(step)) with { Key = "login-step:" + step };

        /// <summary>"Is this you?": a plain 68-DIP account row — no green pill, no auto-advance. The name arrives with the
        /// account's user row (<c>UserFields.Identity</c>); until then the account id stands in (<c>DisplayNameFor</c>).</summary>
        static Element DoneBody()
        {
            _ = Entities.Current.Users.Changed.Value;   // subscribe: the account's identity lands a tick after Online
            User me = User.Me;
            bool known = me.IsValid && me.Knows(UserFields.Identity);
            Spotify.Session s = Spotify.Current;
            string account = s.Username.IsEmpty ? "" : Entities.Strings.Resolve(s.Username);
            string name = SignInPresentation.DisplayNameFor(known ? new AccountName(account, Entities.Strings.Resolve(me.NameId)) : null, null) ?? account;
            return SetupText.Stack(
                SetupText.Lead(Loc.Get(Strings.Setup.SignIn.ConfirmLead)),
                new BoxEl
                {
                    MinHeight = SettingsCard.MinHeight, Padding = Edges4.All(SettingsCard.Padding),
                    Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault, Corners = CornerRadius4.All(Radii.Control),
                    Direction = 0, Gap = Spacing.L, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        PersonPicture.Create("", 40f, displayName: name, imageSourcePath: known ? Controls.ArtUrl(me.ImageId) : null) with { Shrink = 0f },
                        new BoxEl
                        {
                            Direction = 1, Gap = 2f, Grow = 1f, Basis = 0f, MinWidth = 0f,
                            Children =
                            [
                                BodyStrong(name) with { MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                                Caption(Loc.Get(s.Tier == Spotify.Tier.Premium ? Strings.Auth.PremiumBadge : Strings.Setup.SignIn.Free)),
                            ],
                        },
                        HyperlinkButton.Create(Loc.Get(Strings.Setup.SignIn.NotMe), SwitchAccount, size: ControlSize.Small),
                    ],
                },
                SetupText.Secondary(Loc.Get(Strings.Setup.SignIn.ConfirmHint)));
        }
    }

    /// <summary>One rung of the Busy ladder: a 16 ring while current, an accent check when done, a tertiary bullet pending,
    /// a critical cross on failure — each mark keyed by its state so it scale-pops.</summary>
    sealed class LoginStepRow(int step) : Component
    {
        public override Element Render()
        {
            Spotify.SignInState st = Spotify.SignIn.State.Value;
            Spotify.SessionPhase phase = Spotify.Status.Value;
            int cur = Commands.LoginStep(st.Handed, phase);
            bool done = cur > step, current = cur == step, failed = current && phase == Spotify.SessionPhase.Failed;
            Element mark = current && !failed
                ? ProgressRing.Indeterminate(16f)
                : new TextEl(failed ? Icons.Cancel : done ? Icons.Accept : Icons.RadioBullet)
                {
                    Size = failed || done ? 15f : 11f, FontFamily = Theme.IconFont,
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
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Height = 26f, Width = 300f, Shrink = 0f,
                Enter = new EnterExit(Dx: -6f, Opacity: 0f, Active: true), Transition = MotionTok.ControlNormal,
                Children =
                [
                    new BoxEl { Width = 18f, Height = 18f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [mark] },
                    new TextEl(Loc.Get(Commands.LoginStepKey(step)))
                    {
                        Size = 12f, LineHeight = 16f, Weight = current ? (ushort)600 : (ushort)400,
                        Color = current ? Tok.TextPrimary : done ? Tok.TextSecondary : Tok.TextTertiary,
                    },
                ],
            };
        }
    }

    /// <summary>The ladder's determinate bar (220), its fill reflow-eased between rungs; Error on a failed login.</summary>
    sealed class LoginStepBar : Component
    {
        static readonly TemplateParts FillEase = new()
        {
            [ProgressBar.PartFill] = b => b with
            {
                Layout = new LayoutTransition(TransitionChannels.Size, MotionTok.ControlNormal.ToDynamics(),
                    Size: SizeMode.Reflow, Anchor: SizeAnchor.Leading, Axes: SizeAxes.Width),
            },
        };

        public override Element Render()
        {
            Spotify.SignInState st = Spotify.SignIn.State.Value;
            Spotify.SessionPhase phase = Spotify.Status.Value;
            return ProgressBar.Determinate(Commands.LoginStep(st.Handed, phase) / (float)Commands.LoginSteps, 220f,
                phase == Spotify.SessionPhase.Failed ? ProgressBarState.Error : ProgressBarState.Normal, FillEase);
        }
    }

    // ── page 2 · Local playback (owner I's runtime model and pieces, in the chapter's column) ──────────────────────

    sealed class LocalPlaybackPage(WizardSession session) : Component
    {
        public override Element Render()
        {
            var overlay = UseContext(Overlay.Service);
            if (session.Model is not { } m)   // no provisioning host (D3): the model-null arm — no Key, no motion, no fine print
                return WizardFrame(WizardPage.LocalPlayback, Loc.Get(Commands.RuntimeHeaderKey(null)), SetupText.Stack(
                    SetupText.Lead(Loc.Get(Commands.RuntimeLeadKey(null))),
                    SetupText.Body(Loc.Get(Strings.Playback.Runtime.NotActive))));

            RuntimePhase phase = m.Phase.Value;
            string arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString();
            var kids = new List<Element>(3) { SetupText.Lead(Loc.Get(Commands.RuntimeLeadKey(phase))), Content(m, phase, arch, overlay) };
            if (phase != RuntimePhase.Advanced)
                kids.Add(SetupText.Secondary(Loc.Get(Strings.Setup.LocalPlayback.FinePrint)) with { Key = "runtime:fineprint" });
            Element body = SetupText.Stack(kids.ToArray()) with
            {
                Key = "runtime:" + phase, Enter = BodyEnter, Exit = BodyExit, Transition = MotionTok.StandardEnter,
            };
            return WizardFrame(WizardPage.LocalPlayback, Loc.Get(Commands.RuntimeHeaderKey(phase)), body);
        }

        static Element Content(RuntimeModel m, RuntimePhase phase, string arch, IOverlayService overlay) => phase switch
        {
            RuntimePhase.Offer => SetupText.Group(OfferCard(arch), SettingsExpander.Create(new SettingsExpander.Options
            {
                Header = Loc.Get(Strings.Setup.LocalPlayback.Advanced),
                HeaderIcon = Icons.Settings,
                Items =
                [
                    SettingsExpander.Item(Loc.Get(Strings.Setup.LocalPlayback.AdvancedDll), Loc.Get(Strings.Setup.LocalPlayback.AdvancedDllSub),
                        isClickEnabled: true, onClick: m.PickFolder),
                    SettingsExpander.Item(Loc.Get(Strings.Setup.LocalPlayback.AdvancedInstalled), Loc.Get(Strings.Setup.LocalPlayback.AdvancedInstalledSub),
                        isClickEnabled: true, onClick: m.UseInstalled),
                    SettingsExpander.Item(Loc.Get(Strings.Setup.LocalPlayback.AdvancedVersion), Loc.Get(Strings.Setup.LocalPlayback.AdvancedVersionSub),
                        isClickEnabled: true, onClick: m.ShowAdvanced),
                ],
            })),
            RuntimePhase.FetchingCatalog => SetupText.Card(Loc.Get(Strings.Playback.Runtime.CheckingSupport),
                Loc.Get(Strings.Playback.Runtime.ReachingCatalog) + "  ·  " + arch, Icons.Download, ProgressBar.Indeterminate(Layout.ProgressWidth)),
            RuntimePhase.Downloading => Embed.Comp(() => new DownloadCard(m)),
            RuntimePhase.Verifying => SetupText.Card(Loc.Get(Strings.Playback.Runtime.Verifying),
                Loc.Get(Strings.Playback.Runtime.VerifyingCaption), Icons.Download, ProgressBar.Indeterminate(Layout.ProgressWidth)),
            RuntimePhase.Untrusted => SetupText.Group(
                InfoBar.Create(InfoBarSeverity.Warning, Loc.Get(Strings.Playback.Runtime.SignatureInvalid), Loc.Get(Strings.Playback.Runtime.UntrustedBody), isClosable: false),
                SetupText.Card(Loc.Get(Strings.Playback.Runtime.DetailVersion), m.ActiveEntry is { } e ? e.Version + "  ·  " + e.Arch : "—"),
                SetupText.Card(Loc.Get(Strings.Playback.Runtime.DetailSha256), m.ActiveEntry is { } h ? RuntimeRules.ShortHash(h.Sha256) : "—")),
            RuntimePhase.Ready => Ready(m, overlay),
            RuntimePhase.Failed => SetupText.Group(
                InfoBar.Create(InfoBarSeverity.Error, Loc.Get(Strings.Playback.Runtime.Missing), m.Error.Value ?? Loc.Get(Strings.Playback.Runtime.NoPack), isClosable: false),
                OfferCard(arch)),
            _ => Advanced(m),
        };

        static Element OfferCard(string arch) =>
            SetupText.Card(Loc.Get(Strings.Setup.LocalPlayback.CardTitle), Strings.Setup.LocalPlayback.CardSub(arch), Icons.Download);

        static Element Ready(RuntimeModel m, IOverlayService overlay)
        {
            RuntimeFacts facts = m.Status;
            string? location = facts.Location;
            var kids = new List<Element>(5);
            if (m.UpToDate.Value) kids.Add(SetupText.Body(Loc.Get(Strings.Playback.Runtime.UpToDate)));
            kids.Add(SetupText.Card(Loc.Get(Strings.Setup.LocalPlayback.Version), RuntimeRules.OrDash(facts.Version)));
            kids.Add(SetupText.Card(Loc.Get(Strings.Setup.LocalPlayback.Architecture), RuntimeRules.OrDash(facts.Arch)));
            kids.Add(SetupText.Card(Loc.Get(Strings.Setup.LocalPlayback.Signature), RuntimeSignatureSummary(facts),
                content: facts.Signature is null ? null
                    : HyperlinkButton.Create(Loc.Get(Strings.Setup.LocalPlayback.View), () => Shell.OpenSignatureDialog(overlay, facts))));
            kids.Add(SetupText.Card(Loc.Get(Strings.Setup.LocalPlayback.Location), RuntimeRules.OrDash(location),
                content: HyperlinkButton.Create(Loc.Get(Strings.Setup.LocalPlayback.OpenFolder), () => RevealFolder(location))));
            return SetupText.Group(kids.ToArray());
        }

        /// <summary>The Advanced arm — owner I's catalog arms and pieces (<see cref="RuntimeVersionPicker"/>,
        /// <see cref="RuntimeLocalSourceRows"/>), mounted as the standalone card mounts them.</summary>
        static Element Advanced(RuntimeModel m)
        {
            var kids = new List<Element>(5) { RuntimeText(Loc.Get(Strings.Playback.Runtime.AdvancedBody)) };
            switch (RuntimeRules.AdvancedArm(m.Catalog.Value, m.SupportedPacks.Count))
            {
                case RuntimeAdvancedArm.Busy: kids.Add(RuntimeBusyRow(Loc.Get(Strings.Playback.Runtime.CheckingSupport))); break;
                case RuntimeAdvancedArm.Picker: kids.Add(RuntimeVersionPicker(m, null)); break;
                case RuntimeAdvancedArm.NoPack: kids.Add(RuntimeText(Loc.Get(Strings.Playback.Runtime.NoPack))); break;
                case RuntimeAdvancedArm.Unreachable: kids.Add(RuntimeText(Loc.Get(Strings.Playback.Runtime.CatalogUnreachable))); break;
            }
            kids.AddRange(RuntimeLocalSourceRows(m));
            return SetupText.Group(kids.ToArray());
        }

        static void RevealFolder(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex) { Log.Warn("setup", "open the runtime folder failed", ex); }
        }
    }

    /// <summary>The Downloading card on its own component, so a byte tick re-renders one card, never the page and its hero.</summary>
    sealed class DownloadCard(RuntimeModel m) : Component
    {
        public override Element Render()
        {
            long received = m.Received.Value, total = m.Total.Value;
            return SetupText.Card(m.DownloadLabel.Value ?? Loc.Get(Strings.Playback.Runtime.Downloading),
                RuntimeRules.DownloadBytes(received, total), Icons.Download,
                total > 0 ? ProgressBar.Determinate(RuntimeRules.ProgressFraction(received, total), Layout.ProgressWidth)
                          : ProgressBar.Indeterminate(Layout.ProgressWidth));
        }
    }

    // ══ END REGION (WP-6.R) ═════════════════════════════════════════════════════════════════════════════════════════
}
