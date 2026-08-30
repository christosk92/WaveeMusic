using System;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// ── The login SIGN-IN PARTS (a static holder, not a screen) ──────────────────────────────────────────────────────────
// This file used to BE the full-screen login takeover: a Component that projected bridge.Login into a two-pane
// AwaitingApproval card plus four narrow status cards. That component is DELETED. The setup wizard
// (Features/Setup/SetupPage.SignIn.cs) is now Wavee's one and only sign-in surface, and WaveeApp's gate mounts exactly
// two leaves — WaveeShell when authed, SetupPreAuthRoot otherwise — so nothing could reach the takeover any more.
// Shipping both meant the same action looked different in two places, which is the duplication the wizard exists to end.
//
// What survives is what the wizard actually composes: the Spotify brand tints, the pairing lane (CompactRightPane), the
// OR divider, the browser-login button, the terminal glyph badge, the OpenUrl hop, and the four live sub-components
// below (LoginStepRow/LoginStepBar for the Finalizing ladder, WaitingDots, LoginCountdown).
//
// The name stays `LoginView` deliberately: it is referenced as `LoginView.OpenUrl` / `LoginView.SpotifyGreen` from
// Home, the notification panel, the profile menu, Settings and the wizard, and renaming it would churn a dozen call
// sites in other people's files to say nothing new.
static class LoginView
{
    internal const string CodeFont = "Consolas";   // a monospace face for the pairing code (Windows-resident; the app is Win-only)
    internal static readonly ColorF SpotifyGreen = ColorF.FromRgba(0x1D, 0xB9, 0x54);
    // internal (not private/static-only-here): the setup wizard's sign-in page (Work package C) reuses this exact
    // tint for its own Premium terminal-stage GlyphBadge, so the two Premium glyphs (takeover + wizard) can never
    // drift apart into two slightly different golds.
    internal static readonly ColorF GoldTint = ColorF.FromRgba(0xE9, 0xC4, 0x6A);

    // ── shared chrome ────────────────────────────────────────────────────────────────────────────────────────────────
    internal static Element GlyphBadge(string glyph, ColorF tint) => new BoxEl
    {
        Width = 52f, Height = 52f, Corners = CornerRadius4.All(16f),
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Fill = ColorF.Lerp(Tok.FillCardSecondary, tint, 0.22f),
        Children = [new TextEl(glyph) { Size = 26f, FontFamily = Theme.IconFont, Color = tint }],
    };

    // `compact`: the wizard's decision column wants a 28-DIP brand row; the no-arg default is the full 56-DIP wordmark
    // the wizard's own wider sign-in body still uses. One method with an overload, not two, so the two wordmarks cannot
    // drift into slightly different greens.
    internal static Element SpotifyBrand(bool compact = false) => compact
        ? new BoxEl
        {
            Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, Height = 28f, Shrink = 0f,
            Children =
            [
                new TextEl(Icons.MusicNote) { Size = 20f, FontFamily = Theme.IconFont, Color = SpotifyGreen },
                new TextEl("Spotify") { Size = 18f, Weight = 700, Color = SpotifyGreen },
            ],
        }
        : new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            Children =
            [
                new TextEl(Icons.MusicNote) { Size = 30f, FontFamily = Theme.IconFont, Color = SpotifyGreen },
                new TextEl("Spotify") { Size = 26f, Weight = 700, Color = SpotifyGreen },
            ],
        };

    internal static Element BrowserLoginButton(Action onClick) => new BoxEl
    {
        AlignSelf = FlexAlign.Stretch, Direction = 0, Gap = Spacing.S,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, MinHeight = 48f,
        Corners = Radii.ControlAll, Fill = Tok.AccentDefault,
        HoverFill = Tok.AccentSecondary, PressedFill = Tok.AccentTertiary,
        BrushTransitionMs = Motion.ControlFaster, Role = AutomationRole.Button,
        Focusable = true, OnClick = onClick,
        Children =
        [
            new TextEl(Loc.Get(Strings.Auth.LogIn)) { Size = 15f, Weight = 600, Color = Tok.TextOnAccentPrimary },
            new TextEl(Icons.OpenInNewWindow) { Size = 14f, FontFamily = Theme.IconFont, Color = Tok.TextOnAccentPrimary },
        ],
    };

    internal static void OpenUrl(string url) => InputHooks.Current.Default.OpenUri?.Invoke(url);

    // ── the QR pane + OR divider the setup wizard's SignIn page composes. Re-authoring either one over there would
    // duplicate this column pixel-for-pixel, which is exactly the drift this repo keeps catching.

    internal static Element OrDivider(float width = 48f, bool horizontal = false) => horizontal
        ? new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            AlignSelf = FlexAlign.Stretch, Gap = Spacing.S,
            Children =
            [
                new BoxEl { Height = 1f, Grow = 1f, Fill = Tok.StrokeDividerDefault },
                new BoxEl
                {
                    Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS),
                    Corners = CornerRadius4.All(Radii.Pill), Fill = Tok.FillSubtleSecondary,
                    Children = [WaveeType.Eyebrow(Loc.Get(Strings.Auth.Or)) with { Color = Tok.TextSecondary }],
                },
                new BoxEl { Height = 1f, Grow = 1f, Fill = Tok.StrokeDividerDefault },
            ],
        }
        : new BoxEl
    {
        Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Width = width, Gap = Spacing.S,
        Padding = new Edges4(0f, Spacing.S, 0f, Spacing.S),
        Children =
        [
            new BoxEl { Width = 1f, Grow = 1f, Fill = Tok.StrokeDividerDefault },
            new BoxEl
            {
                Padding = new Edges4(Spacing.S, Spacing.XS, Spacing.S, Spacing.XS),
                Corners = CornerRadius4.All(Radii.Pill), Fill = Tok.FillSubtleSecondary,
                Children = [WaveeType.Eyebrow(Loc.Get(Strings.Auth.Or)) with { Color = Tok.TextSecondary }],
            },
            new BoxEl { Width = 1f, Grow = 1f, Fill = Tok.StrokeDividerDefault },
        ],
    };

    /// <summary>The setup wizard's deliberately lean 196-DIP pairing lane: the approved prototype's 138-DIP QR,
    /// pairing link, code, expiry and waiting state. No Copy/Open buttons — the wide takeover pane that had room for
    /// them is gone, and the code is short enough to type from the screen it is shown on.</summary>
    // `interactive = true` keeps every existing call site (the setup wizard's own pairing pane at Idle) byte-identical;
    // the wizard passes `false` while the pane is faded to a 22% Busy reminder — it stays MOUNTED (the cross-fade needs
    // it there to fade FROM) but must stop being a Tab stop / hit-test target at that opacity (SetupPage.SignIn.cs).
    internal static BoxEl CompactRightPane(LoginChallenge c, bool interactive = true) => new BoxEl
    {
        Direction = 1, Width = SetupLayout.CompactPairingWidth, Shrink = 0f,
        Gap = Spacing.S, AlignItems = FlexAlign.Center, Justify = FlexJustify.Start,
        Padding = new Edges4(Spacing.M, Spacing.XXS, Spacing.M, Spacing.S),
        Children =
        [
            Embed.Comp(() => new QrGrid(c.VerificationUriComplete ?? c.VerificationUri, SetupLayout.CompactQrSize)),
            BodyStrong(Loc.Get(Strings.Auth.ScanToLogIn)),
            new BoxEl
            {
                Direction = 0, Wrap = true, Gap = Spacing.XS, MinWidth = 0f,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, AlignSelf = FlexAlign.Stretch,
                Children =
                [
                    Caption(Loc.Get(Strings.Auth.OrGoTo)).Secondary(),
                    CompactPairingLink(Loc.Get(Strings.Auth.PairUrl), c.VerificationUri, interactive),
                    Caption(Loc.Get(Strings.Auth.EnterCodeColon)).Secondary(),
                ],
            },
            new TextEl(c.UserCode)
            {
                Size = 21f, Weight = 700, CharSpacing = 70f, FontFamily = CodeFont,
                Color = Tok.TextPrimary, MaxLines = 1,
            },
            Embed.Comp(() => new LoginCountdown(c.Expiry, compact: true)),
            new BoxEl
            {
                Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center,
                Children = [Caption(Loc.Get(Strings.Auth.WaitingApproval)).Secondary(), Embed.Comp(() => new WaitingDots())],
            },
        ],
    };

    static Element CompactPairingLink(string text, string url, bool interactive = true) => new BoxEl
    {
        Padding = new Edges4(Spacing.XXS, 0f, Spacing.XXS, 0f), Corners = CornerRadius4.All(Radii.Control),
        Role = AutomationRole.Hyperlink, Focusable = interactive, HitTestVisible = interactive,
        Cursor = CursorId.Hand, OnClick = () => OpenUrl(url),
        Children = [new TextEl(text) { Size = 12.5f, Weight = 600, Color = Tok.AccentTextPrimary, MaxLines = 1 }],
    }.Interactive(Interaction.Subtle);
}

// ── One row of the Finalizing step list ──────────────────────────────────────────────────────────────────────────────
// Reads the login SIGNAL rather than taking the step as a prop: props freeze at mount (component-props contract), so a
// parent re-render would never move a field-passed step. The row's OWN step and label are constants, which is exactly
// what a frozen prop is for.
//
// Marks: pending = a dim bullet; current = the shipped indeterminate ProgressRing (reduced-motion aware, compositor
// driven — no Lottie in this engine and none needed); done = a checkmark with an explicit scale-POP, the same
// anim-keyframe path WaitingDots uses rather than a keyed Enter the positional reconciler can update in place.
sealed class LoginStepRow : Component
{
    readonly Signal<LoginSnapshot> _login;
    readonly LoginStep _step;
    readonly string _label;
    readonly float _width;
    // `width = NaN` (BoxEl.Width's own "unconstrained" default) = an unconstrained row in a single tall column; the
    // setup wizard's Busy approve card passes a fixed width so four rows wrap into a tidy 2×2 grid instead.
    public LoginStepRow(Signal<LoginSnapshot> login, LoginStep step, string label, float width = float.NaN)
    { _login = login; _step = step; _label = label; _width = width; }

    public override Element Render()
    {
        var snap = _login.Value;   // subscribe → re-render as the bootstrap advances
        int mine = (int)_step, cur = (int)snap.Step;
        bool done = cur > mine;
        bool current = cur == mine;
        bool failed = current && snap.Phase == LoginPhase.Failed;

        Element mark = current && !failed
            ? ProgressRing.Indeterminate(16f)
            : new TextEl(failed ? Icons.Cancel : done ? Icons.Accept : Icons.RadioBullet)
            {
                Size = failed || done ? 15f : 11f,
                FontFamily = Theme.IconFont,
                Color = failed ? Tok.SystemFillCritical : done ? Tok.AccentDefault : Tok.TextTertiary,
            };
        string markState = failed ? "failed" : done ? "done" : current ? "current" : "pending";
        mark = mark with
        {
            Key = "login-step-mark:" + markState,
            Enter = new EnterExit(Sx: 0.72f, Sy: 0.72f, Opacity: 0f, Active: true),
            Exit = new EnterExit(Sx: 0.88f, Sy: 0.88f, Opacity: 0f, Active: true),
            Transition = MotionTok.ControlNormal,
        };

        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Height = 26f,
            Width = _width, Shrink = 0f,
            Enter = new EnterExit(Dx: -6f, Opacity: 0f, Active: true), Transition = MotionTok.ControlNormal,
            Children =
            [
                new BoxEl
                {
                    Width = 18f, Height = 18f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [mark],
                },
                new TextEl(_label)
                {
                    Size = 12f, LineHeight = 16f,
                    // The current step is the one the user is waiting on — give it the primary weight and let the
                    // finished and not-yet-started rows recede.
                    Weight = current ? (ushort)600 : (ushort)400,
                    Color = current ? Tok.TextPrimary : done ? Tok.TextSecondary : Tok.TextTertiary,
                },
            ],
        };
    }
}

// ── The Finalizing progress bar ──────────────────────────────────────────────────────────────────────────────────────
// Determinate off the step index, so the bar means something instead of sweeping forever. The stock ProgressBar remains
// the primitive; its Fill part opts into token-driven width reflow so fast bootstrap writes ease between their real
// values instead of flashing through four hard width snaps.
sealed class LoginStepBar : Component
{
    static readonly LayoutTransition FillEase = new(
        TransitionChannels.Size,
        MotionTok.ControlNormal.ToDynamics(),
        Size: SizeMode.Reflow,
        Anchor: SizeAnchor.Leading,
        Axes: SizeAxes.Width);

    static readonly TemplateParts ProgressParts = new()
    {
        [ProgressBar.PartFill] = b => b with { Layout = FillEase },
    };

    readonly Signal<LoginSnapshot> _login;
    public LoginStepBar(Signal<LoginSnapshot> login) { _login = login; }

    public override Element Render()
    {
        var snap = _login.Value;   // subscribe → re-render as the bootstrap advances
        const float Steps = 4f;    // Connecting / Metadata / Audio / Profile — Done means all four landed
        float v = Math.Clamp((int)snap.Step / Steps, 0f, 1f);
        return ProgressBar.Determinate(v, 220f,
            snap.Phase == LoginPhase.Failed ? ProgressBarState.Error : ProgressBarState.Normal,
            ProgressParts);
    }
}

// ── The animated "waiting" dots — a left-to-right opacity pulse (the prototype's authorize indicator) ─────────────────
// Each dot loops an opacity wave whose peak is phase-shifted, so the highlight travels across the three. Driven on the
// compositor via AnimEngine.Keyframes (no per-frame re-render); captured node handles, like ProgressBar's IndeterminateBar.
sealed class WaitingDots : Component
{
    public override Element Render()
    {
        var d0 = UseRef<NodeHandle>(default);
        var d1 = UseRef<NodeHandle>(default);
        var d2 = UseRef<NodeHandle>(default);
        UseEffect(() =>
        {
            if (Motion.ReducedMotion) return;   // reduced motion: leave the dots static (no pulse)
            var anim = Context.Anim;
            var scene = Context.Scene;
            if (anim is null || scene is null) return;
            void Drive(NodeHandle h, float peak)
            {
                if (h.IsNull || !scene.IsLive(h)) return;
                anim.Keyframes(h, AnimChannel.TranslateY, new Keyframe[]
                {
                    new(0f, 0f, Easing.EaseInOut),
                    new(peak, -5f, Easing.EaseInOut),                          // hop up
                    new(MathF.Min(peak + 0.26f, 0.98f), 0f, Easing.EaseInOut), // settle back
                    new(1f, 0f, Easing.Linear),
                }, 1100f, loop: true, displayRate: true);
            }
            Drive(d0.Value, 0.16f);   // STAGGERED peaks → a left-to-right hop wave (dot0 → dot1 → dot2)
            Drive(d1.Value, 0.28f);
            Drive(d2.Value, 0.40f);
        }, DepKey.Empty);
        Element Dot(Action<NodeHandle> cap) => new BoxEl { Width = 6f, Height = 6f, Corners = CornerRadius4.All(3f), Fill = Tok.AccentDefault, OnRealized = cap };
        return new BoxEl { Direction = 0, Gap = 5f, AlignItems = FlexAlign.Center, Children = [Dot(h => d0.Value = h), Dot(h => d1.Value = h), Dot(h => d2.Value = h)] };
    }
}

// ── The pairing-code expiry countdown ("Expires in mm:ss") — a per-SECOND signal write, never a per-frame read ────────
sealed class LoginCountdown : Component
{
    readonly DateTimeOffset _expiry;
    readonly bool _compact;
    public LoginCountdown(DateTimeOffset expiry, bool compact = false) { _expiry = expiry; _compact = compact; }

    public override Element Render()
    {
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
        _ = tick.Value;   // subscribe → re-render each second

        var remaining = _expiry - DateTimeOffset.UtcNow;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        string txt = ((int)remaining.TotalMinutes).ToString("00") + ":" + remaining.Seconds.ToString("00");
        if (_compact)
            return new TextEl(Strings.Auth.ExpiresIn(txt)) { Size = 11.5f, Color = Tok.TextTertiary };

        return new BoxEl
        {
            Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center,
            Padding = new Edges4(10, 4, 11, 5), Corners = CornerRadius4.All(11f), Fill = Tok.FillSubtleSecondary,
            Children =
            [
                new TextEl(Icons.Clock) { Size = 12f, FontFamily = Theme.IconFont, Color = Tok.TextTertiary },
                new TextEl(Strings.Auth.ExpiresIn(txt)) { Size = 12f, Color = Tok.TextSecondary },
            ],
        };
    }
}
