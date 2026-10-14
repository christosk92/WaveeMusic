// ── Themes/Classic/Structure/Views/Screens/ClassicSetup.cs — Classic's setup screens (themes plan 6.1, T11c) ───────────────────
//
// From `Screens\Setup.UI.cs`, `Setup.UI.Runtime.cs`, `Setup.UI.Theme.cs`: the wizard's plate (`Setup.Wizard`), its footer, the page frame (icon column, title, scrolling body), the Lottie
// heroes' recolour seam and the five-primitive page vocabulary. The pages are `ClassicSetup.Pages.cs` (Terms, Theme, Local playback), the sign-in surfaces `ClassicSetup.SignIn.cs`
// (the door, the Sign in page, the pairing card, the QR grid, the Busy ladder) and the runtime card `ClassicSetup.Runtime.cs` (its body, its command row and the pieces the wizard's Local playback
// page reuses). Every decision is core: `Setup.WizardModel` (page, direction, the footer row, the verbs), `Setup.Gating`, `Setup.SignInRules`, `Setup.RuntimeRules` and the models; this
// code lays out and wires.
//
//   Setup.Wizard   762×490 plate · FillSolidBase · r8 · 1px StrokeSurfaceDefault · Elevation.Dialog · Enter / Backspace
//   ├ content  FillLayerAlt, pad 24 → Flow.KeepAlive(page; 3 entries; Nav.RecipeFor(Dir.Peek()); no replay on activate)
//   │   └ Terms | Sign in | Theme | Local playback → WizardFrame = [192 Lottie hero] · Title · ScrollView(body)
//   ├ separator 1 StrokeCardDefault
//   ├ WizardFooter 80 = [210: step label + 162 bar] · PRIMARY (left) · SECONDARY (right)      ← Setup.Commands.ResolveFor
//   └ [Theme, Local playback] Back 30×30 over the content region's 24-DIP corner

using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming.Contracts;
using static FluentGpu.Dsl.Ui;

namespace Wavee.Themes.Classic;

/// <summary>Wavee Classic's setup screens.</summary>
internal static partial class ClassicSetup
{
    /// <summary>Registers the seven setup keys (<c>ClassicStructure.Register</c> calls it once).</summary>
    public static void Register(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.SetupWizard, WizardTemplate, "classic.setup.wizard");
        views.Add(ViewKeys.SetupTerms, TermsTemplate, "classic.setup.terms");
        views.Add(ViewKeys.SetupSignIn, SignInPageTemplate, "classic.setup.sign-in");
        views.Add(ViewKeys.SetupTheme, ThemeTemplate, "classic.setup.theme");
        views.Add(ViewKeys.SetupLocalPlayback, LocalPlaybackTemplate, "classic.setup.local-playback");
        views.Add(ViewKeys.SetupSignInDoor, SignInDoorTemplate, "classic.setup.sign-in-door");
        views.Add(ViewKeys.SetupRuntimeCard, RuntimeCardTemplate, "classic.setup.runtime-card");
    }

    // ── the plate ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The registered template of <see cref="ViewKeys.SetupWizard"/>.</summary>
    public static Element WizardTemplate(SetupWizardProps props, in ViewContext ctx)
    {
        var session = props.Session;
        Size2 vp = ctx.Hooks.UseContextSignal(Viewport.Size).Value;
        Setup.WizardPage page = session.Page.Value;

        void OnKey(KeyEventArgs e)
        {
            if (e.Handled) return;
            if (e.KeyCode == Keys.Enter && session.Row().PrimaryEnabled) { session.Primary(); e.Handled = true; }
            else if (e.KeyCode == Keys.Back && Setup.Gating.ShowsBack(session.Page.Peek())) { session.Back(); e.Handled = true; }
        }

        // PagesHost claims Grow/Shrink/Min 0 so the keep-alive (a ComponentEl with no layout columns) gets a DEFINITE box
        // and the page's ScrollView scrolls instead of shrink-wrapping and clipping (ch 28 §9.1 #14).
        Element pages = new BoxEl
        {
            Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true,
            Children =
            [
                Flow.KeepAlive(() => session.Page.Value, static p => "setup:page:" + (int)p, p => PageSlot(props, p),
                    new KeepAliveOptions(MaxEntries: SetupGeometry.KeepAlivePages, TransitionFor: (_, _) => Design.Nav.RecipeFor(session.Direction.Peek()),
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
                    Grow = 1f, Shrink = 1f, MinHeight = 0f, Fill = Tok.FillLayerAlt, Padding = Edges4.All(SetupGeometry.PlatePadding),
                    Children = [pages],
                },
                new BoxEl { Height = SetupGeometry.SeparatorHeight, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeCardDefault },
                Embed.Comp(() => new WizardFooter(session)),
            ],
        };
        return new BoxEl
        {
            ZStack = true,
            Width = SetupGeometry.Width(vp.Width), Height = SetupGeometry.Height(vp.Height), MinWidth = SetupGeometry.MinPlateWidth, MinHeight = SetupGeometry.MinPlateHeight,
            Corners = Radii.OverlayAll, Fill = Tok.FillSolidBase, BorderWidth = 1f, BorderColor = Tok.StrokeSurfaceDefault,
            Shadow = Elevation.Dialog, ClipToBounds = true, OnKeyDown = OnKey,
            Children = Setup.Gating.ShowsBack(page) ? new[] { chrome, BackOverlay(session) } : new[] { chrome },
        };
    }

    // Over the content region's own 24-DIP corner, so it reads as part of the header row.
    private static Element BackOverlay(Setup.WizardModel session) => new BoxEl
    {
        Key = "setup:back", Direction = 0, AlignItems = FlexAlign.Start, Justify = FlexJustify.Start,
        Padding = Edges4.All(SetupGeometry.PlatePadding), HitTestPassThrough = true,
        Children =
        [
            IconButton.Create(Icons.Back, session.Back,
                style: IconButton.DefaultStyle with { Size = SetupGeometry.BackButtonSize, GlyphSize = SetupGeometry.BackGlyphSize }),
        ],
    };

    private static Element PageSlot(SetupWizardProps props, Setup.WizardPage page) => new BoxEl
    {
        Key = "setup:capture:" + (int)page, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
        Children =
        [
            page switch
            {
                Setup.WizardPage.Terms => Slots.View(ViewKeys.SetupTerms, props.Terms) with { Key = "setup:page:terms" },
                Setup.WizardPage.SignIn => Slots.View(ViewKeys.SetupSignIn, props.SignIn) with { Key = "setup:page:sign-in" },
                Setup.WizardPage.Theme => Slots.View(ViewKeys.SetupTheme, props.Theme) with { Key = "setup:page:theme" },
                _ => Slots.View(ViewKeys.SetupLocalPlayback, props.LocalPlayback) with { Key = "setup:page:local-playback" },
            },
        ],
    };

    // ── the footer (Rise's ControlGrid: primary LEFT, secondary RIGHT) ──────────────────────────────────────────────

    private sealed class WizardFooter(Setup.WizardModel session) : Component
    {
        public override Element Render()
        {
            bool large = SetupGeometry.ShowsIcon(UseContextSignal(Viewport.Size).Value.Width);
            Setup.CommandRow row = session.Row();

            var kids = new List<Element>(3);
            if (large)
            {
                var step = session.StepNumber();
                kids.Add(new BoxEl
                {
                    // Pinned to the button lane; the 48 is a RIGHT pad (Edges4 is L, T, R, B — the shipped footer-band bug).
                    Width = SetupGeometry.ProgressColumnFor(true), Height = ClassicSetupRules.FooterButtonHeight, Shrink = 0f, Direction = 1,
                    Padding = new Edges4(0f, 0f, SetupGeometry.ProgressColumnRightPad, 0f), Gap = SetupGeometry.ProgressStackGap,
                    Justify = FlexJustify.Center, AlignItems = FlexAlign.Start, AlignSelf = FlexAlign.Center,
                    Children =
                    [
                        new TextEl(step is { } n ? Strings.Setup.StepOf(n.Step, n.Total) : Loc.Get(Strings.Setup.PreSetup))
                        {
                            Size = ClassicSetupRules.FooterStepLabelSize, Weight = 600, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                        ProgressBar.Determinate(session.Progress(), SetupGeometry.ProgressWidth),
                    ],
                });
            }
            string primary = Loc.Get(row.PrimaryKey ?? Strings.Setup.Accept);
            kids.Add(Stretch(row.PrimaryKind == Setup.ButtonKind.Standard
                ? Button.Standard(primary, session.Primary, isEnabled: row.PrimaryEnabled)
                : Button.Accent(primary, session.Primary, isEnabled: row.PrimaryEnabled)));
            // A null secondary (Verifying, Ready) lets the primary span the whole action lane.
            if (row.SecondaryKey is { } secondary)
                kids.Add(Stretch(Button.Standard(Loc.Get(secondary), session.Secondary, isEnabled: row.SecondaryEnabled)));

            return new BoxEl
            {
                Height = SetupGeometry.FooterHeight, Shrink = 0f, Padding = Edges4.All(SetupGeometry.FooterPadding), Fill = Tok.FillSolidBase,
                Direction = 0, Gap = SetupGeometry.FooterColumnGap, AlignItems = FlexAlign.Center, Children = kids.ToArray(),
            };
        }

        private static BoxEl Stretch(BoxEl button) => button with
        {
            Grow = 1f, Basis = 0f, MinWidth = 0f, Shrink = 1f, Height = ClassicSetupRules.FooterButtonHeight, MinHeight = ClassicSetupRules.FooterButtonHeight, Justify = FlexJustify.Center,
        };
    }

    // ── the page frame (icon column + title + scrolling body) ───────────────────────────────────────────────────────

    /// <summary>A page's frame. The body is rebuilt whenever a page-local signal moves, so it rides RE-PUSHED props (a ctor
    /// arg would freeze the first tree). The outer box claims the parent's Grow/Shrink/Min so the ScrollView is bounded.</summary>
    internal static Element WizardFrame(Setup.WizardPage page, string header, Element body, bool backAutoPadding = true) => new BoxEl
    {
        Key = "setup:frame:" + (int)page, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
        Children = [Embed.Comp(new FrameProps(page, header, body, backAutoPadding), static () => new PageFrame())],
    };

    private sealed record FrameProps(Setup.WizardPage Page, string Header, Element Body, bool BackAutoPadding);

    private sealed class PageFrame : Component
    {
        public override Element Render()
        {
            var p = UseProps<FrameProps>();
            bool iconShown = SetupGeometry.ShowsIcon(UseContextSignal(Viewport.Size).Value.Width);

            Element title = Title(p.Header) with { Grow = 1f, Basis = 0f, MinWidth = 0f, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.WordEllipsis };
            Element header = new BoxEl
            {
                Direction = 0, Shrink = 0f, Margin = new Edges4(0f, -SetupGeometry.HeaderTopPull, 0f, SetupGeometry.HeaderBottomGap),
                Children = p.BackAutoPadding && SetupGeometry.BackSpacerApplies(p.Page, iconShown)
                    ? new[] { new BoxEl { Width = SetupGeometry.BackSpacerWidth, Shrink = 0f }, title }
                    : new[] { title },
            };
            Element scroller = ScrollView(p.Body) with
            {
                Grow = 1f, Shrink = 1f, MinHeight = 0f, MinWidth = 0f,
                // The rail rides in the plate's own 24-DIP pad; AlwaysShowScrollbar so an overflowing lane SAYS it scrolls.
                Margin = new Edges4(0f, 0f, -SetupGeometry.ScrollGutter, 0f), Padding = new Edges4(0f, 0f, SetupGeometry.ScrollGutter, 0f),
                EdgeCues = ScrollEdgeCues.None, AlwaysShowScrollbar = true,
            };
            Element content = new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Children = [header, scroller] };
            if (!iconShown)
                return new BoxEl { Key = "setup:layout:compact", Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Children = [content] };

            FluentGpu.Lottie.LottieSource? source = Setup.WizardHeroes.Hero(p.Page);
            return new BoxEl
            {
                Key = "setup:layout:wide", Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Gap = SetupGeometry.IconColumnGap,
                Children =
                [
                    new BoxEl
                    {
                        Width = SetupGeometry.IconColumnWidth, Shrink = 0f, AlignSelf = FlexAlign.Stretch, Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
                        Children = source is null ? Array.Empty<Element>() : new[] { LottieView.Create(source, SetupGeometry.IconColumnWidth, HeroOptions) },
                    },
                    content,
                ],
            };
        }
    }

    // ── the Lottie heroes ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Rise's cadence (the first half of the timeline, once, then hold) recoloured to the live accent, zoomed 1.2×.
    /// Reduced motion stated explicitly: the static pose at To (never an author-side branch).</summary>
    private static readonly LottieOptions HeroOptions = LottieOptions.RiseSetup with
    {
        Recolor = RecolorLive, Zoom = SetupGeometry.HeroZoom, ReducedMotion = ReducedMotionPolicy.SnapEnd,
    };

    /// <summary>The live seam over <see cref="SetupRecolor.Apply"/>: today's accent tokens, read once per hero mount.</summary>
    private static ColorF RecolorLive(ColorF c) => SetupRecolor.Apply(c, Tok.AccentDefault, Tok.AccentTextPrimary);

    // LoadHero / WarmHeroes: the loader is `Setup.WizardHeroes` in WizardModel.cs — the disk read and the parse run off the UI thread.

    // ── the page body vocabulary: five primitives and a pass-through card ───────────────────────────────────────────

    internal static class SetupText
    {
        public static Element Stack(params Element[] kids) => new BoxEl { Direction = 1, Gap = SetupGeometry.BodySpacing, MinWidth = 0f, Children = kids };
        public static Element Group(params Element[] kids) => new BoxEl { Direction = 1, Gap = SetupGeometry.BodyInnerSpacing, MinWidth = 0f, Children = kids };
        public static Element Lead(string s) => BodyStrong(s) with { Wrap = TextWrap.Wrap, MinWidth = 0f };
        public static Element Body(string s) => Ui.Body(s) with { Wrap = TextWrap.Wrap, MinWidth = 0f };
        public static Element Secondary(string s) => Ui.Body(s).Secondary() with { Wrap = TextWrap.Wrap, MinWidth = 0f };

        /// <summary>The engine SettingsCard as-is, so both of its responsive thresholds (content wrap, icon drop) survive.</summary>
        public static Element Card(string header, string? description, string? glyph = null, Element? content = null) =>
            SettingsCard.Create(new SettingsCard.Options { Header = header, Description = description, HeaderIcon = glyph, Content = content });
    }

    private static readonly EnterExit BodyEnter = new(Dy: ClassicSetupRules.BodyEnterDy, Opacity: 0f, Active: true);
    private static readonly EnterExit BodyExit = new(Dy: ClassicSetupRules.BodyExitDy, Opacity: 0f, Active: true);
}
