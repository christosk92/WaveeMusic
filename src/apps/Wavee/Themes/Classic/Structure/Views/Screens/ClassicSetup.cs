// ── Themes/Classic/Structure/Views/Screens/ClassicSetup.cs — Classic's setup screens (themes plan 6.1, T11c) ───────────────────
//
// From `Screens\Setup.UI.cs`, `Setup.UI.Runtime.cs`, `Setup.UI.Theme.cs`: the wizard's plate (`Setup.Wizard`), its footer, the page frame (icon column, title, scrolling body), the Lottie
// heroes' recolour seam and the five-primitive page vocabulary. The pages are `ClassicSetup.Pages.cs` (Terms, Theme, Local playback), the sign-in surfaces `ClassicSetup.SignIn.cs`
// (the door, the Sign in page, the pairing card, the QR grid, the Busy ladder) and the runtime card `ClassicSetup.Runtime.cs` (its body, its command row and the pieces the wizard's Local playback
// page reuses). Every decision is core: `Setup.WizardModel` (page, direction, the footer row, the verbs), `Setup.Gating`, `Setup.SignInRules`, `Setup.RuntimeRules` and the models; this
// code lays out and wires.
//
//   Setup.Wizard   the plate (762×490, 960×600 at Huge) or, at Compact, a full-bleed SHEET under the title row: the tier's record `ClassicSetupTiers.Of(ctx.Tier())`
//                  (Rules\Tiers.Setup.cs holds every number that differs by window size). FillSolidBase · r8 · 1px StrokeSurfaceDefault · Elevation.Dialog · Enter / Backspace
//   ├ content  FillLayerAlt, the record's padding → Flow.KeepAlive(page; 3 entries; Nav.RecipeFor(Dir.Peek()); no replay on activate)
//   │   └ Terms | Sign in | Theme | Local playback → WizardFrame = [icon column Lottie hero, Standard and Huge] · Title · ScrollView(body)
//   ├ separator 1 StrokeCardDefault
//   ├ WizardFooter 80 = [210: step label + 162 bar] · PRIMARY (left) · SECONDARY (right); a column at Compact   ← Setup.Commands.ResolveFor
//   └ [Theme, Local playback] Back over the icon column's corner (Standard) or the page's own (every other tier); 40 × 40 at Compact

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
        var layout = ClassicSetupTiers.Of(ctx.Tier());   // the window tier's record: final in the first frame, a crossing animates
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
                    Grow = 1f, Shrink = 1f, MinHeight = 0f, Fill = Tok.FillLayerAlt, Padding = layout.ContentPadding,
                    Children = [pages],
                },
                new BoxEl { Height = SetupGeometry.SeparatorHeight, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeCardDefault },
                Embed.Comp(() => new WizardFooter(session)),
            ],
        };
        // The plate (or the Compact sheet): one node in every tier, so a crossing moves and resizes it instead of remounting it.
        Element plate = new BoxEl
        {
            Key = "setup:plate", ZStack = true, Grow = 1f, AlignSelf = FlexAlign.Stretch, MinWidth = layout.MinPlateWidth, MinHeight = layout.MinPlateHeight,
            Corners = layout.Sheet ? default(CornerRadius4) : Radii.OverlayAll, Fill = Tok.FillSolidBase,
            BorderWidth = layout.Sheet ? 0f : 1f, BorderColor = Tok.StrokeSurfaceDefault,
            Shadow = layout.Sheet ? (ShadowSpec?)null : Elevation.Dialog, ClipToBounds = true, OnKeyDown = OnKey,
            Children = Setup.Gating.ShowsBack(page) ? new[] { chrome, BackOverlay(session, layout) } : new[] { chrome },
        };
        // The root is the plate's size (or the window's, for the sheet, whose top padding keeps the title row clear and reachable).
        return new BoxEl
        {
            Key = "setup:root", Direction = 1, Width = layout.RootWidth(vp.Width), Height = layout.RootHeight(vp.Height),
            MinWidth = layout.MinPlateWidth, MinHeight = layout.MinPlateHeight, Padding = new Edges4(0f, layout.RootTopPad, 0f, 0f),
            HitTestPassThrough = layout.Sheet, Layout = Design.Reveal.Resize,
            Children = [plate],
        };
    }

    // Over the page region's own corner (the icon column's at Standard), so it reads as part of the header row.
    private static Element BackOverlay(Setup.WizardModel session, ClassicSetupLayout layout) => new BoxEl
    {
        Key = "setup:back", Direction = 0, AlignItems = FlexAlign.Start, Justify = FlexJustify.Start,
        Padding = layout.BackPadding, HitTestPassThrough = true,
        Children =
        [
            IconButton.Create(Icons.Back, session.Back,
                style: IconButton.DefaultStyle with { Size = layout.BackSize, GlyphSize = SetupGeometry.BackGlyphSize }),
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

    private sealed class WizardFooter(Setup.WizardModel session) : CommonTierComponent
    {
        public override Element Render()
        {
            var layout = ClassicSetupTiers.Of(ReadTier());
            Size2 vp = UseContextSignal(Viewport.Size).Value;
            Setup.CommandRow row = session.Row();

            string primary = Loc.Get(row.PrimaryKey ?? Strings.Setup.Accept);
            var buttons = new List<Element>(2)
            {
                Stretch(row.PrimaryKind == Setup.ButtonKind.Standard
                    ? Button.Standard(primary, session.Primary, isEnabled: row.PrimaryEnabled)
                    : Button.Accent(primary, session.Primary, isEnabled: row.PrimaryEnabled), layout.ButtonHeight),
            };
            // A null secondary (Verifying, Ready) lets the primary span the whole action lane.
            if (row.SecondaryKey is { } secondary)
                buttons.Add(Stretch(Button.Standard(Loc.Get(secondary), session.Secondary, isEnabled: row.SecondaryEnabled), layout.ButtonHeight));

            if (layout.FooterStacked)
            {
                // Compact (C-Setup-Compact): the step label over a bar as wide as the footer, then the buttons as a two-column grid.
                return new BoxEl
                {
                    Key = "setup:footer", Shrink = 0f, Direction = 1, Padding = layout.FooterPadding, Fill = Tok.FillSolidBase, Gap = SetupGeometry.StackedFooterGap,
                    Children =
                    [
                        new BoxEl
                        {
                            Direction = 1, Gap = SetupGeometry.ProgressStackGap, AlignSelf = FlexAlign.Stretch,
                            Children = [StepLabel(session), ProgressBar.Determinate(session.Progress(), layout.ProgressBarWidth(vp.Width, SetupGeometry.ProgressWidth))],
                        },
                        new BoxEl { Direction = 0, Gap = SetupGeometry.StackedButtonGap, AlignSelf = FlexAlign.Stretch, Children = buttons.ToArray() },
                    ],
                };
            }

            var kids = new List<Element>(3)
            {
                new BoxEl
                {
                    // Pinned to the button lane; the 48 is a RIGHT pad (Edges4 is L, T, R, B — the shipped footer-band bug).
                    Width = layout.ProgressColumn, Height = layout.ButtonHeight, Shrink = 0f, Direction = 1,
                    Padding = new Edges4(0f, 0f, SetupGeometry.ProgressColumnRightPad, 0f), Gap = SetupGeometry.ProgressStackGap,
                    Justify = FlexJustify.Center, AlignItems = FlexAlign.Start, AlignSelf = FlexAlign.Center,
                    Children = [StepLabel(session), ProgressBar.Determinate(session.Progress(), SetupGeometry.ProgressWidth)],
                },
            };
            kids.AddRange(buttons);
            return new BoxEl
            {
                Key = "setup:footer", Height = layout.FooterHeight, Shrink = 0f, Padding = layout.FooterPadding, Fill = Tok.FillSolidBase,
                Direction = 0, Gap = SetupGeometry.FooterColumnGap, AlignItems = FlexAlign.Center, Children = kids.ToArray(),
            };
        }

        private static Element StepLabel(Setup.WizardModel session)
        {
            var step = session.StepNumber();
            return new TextEl(step is { } n ? Strings.Setup.StepOf(n.Step, n.Total) : Loc.Get(Strings.Setup.PreSetup))
            {
                Size = ClassicSetupRules.FooterStepLabelSize, Weight = 600, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
            };
        }

        private static BoxEl Stretch(BoxEl button, float height) => button with
        {
            Grow = 1f, Basis = 0f, MinWidth = 0f, Shrink = 1f, Height = height, MinHeight = height, Justify = FlexJustify.Center,
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

    private sealed class PageFrame : CommonTierComponent
    {
        public override Element Render()
        {
            var p = UseProps<FrameProps>();
            var layout = ClassicSetupTiers.Of(ReadTier());

            Element title = Title(p.Header) with { Grow = 1f, Basis = 0f, MinWidth = 0f, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.WordEllipsis };
            Element header = new BoxEl
            {
                Direction = 0, Shrink = 0f, Margin = new Edges4(0f, -SetupGeometry.HeaderTopPull, 0f, SetupGeometry.HeaderBottomGap),
                Children = p.BackAutoPadding && layout.BackSpacerApplies(p.Page)
                    ? new[] { new BoxEl { Width = layout.BackSpacer, Shrink = 0f }, title }
                    : new[] { title },
            };
            // The rail rides in the region's own right padding; AlwaysShowScrollbar so an overflowing lane SAYS it scrolls.
            float gutter = layout.ContentPadding.Right;
            Element scroller = ScrollView(p.Body) with
            {
                Grow = 1f, Shrink = 1f, MinHeight = 0f, MinWidth = 0f,
                Margin = new Edges4(0f, 0f, -gutter, 0f), Padding = new Edges4(0f, 0f, gutter, 0f),
                EdgeCues = ScrollEdgeCues.None, AlwaysShowScrollbar = true,
            };
            Element content = new BoxEl { Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Children = [header, scroller] };
            if (!layout.ShowsIcon)
                return new BoxEl { Key = "setup:layout:compact", Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Children = [content] };

            FluentGpu.Lottie.LottieSource? source = Setup.WizardHeroes.Hero(p.Page);
            return new BoxEl
            {
                Key = "setup:layout:wide", Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Gap = layout.IconGap,
                Children =
                [
                    new BoxEl
                    {
                        Width = layout.IconColumn, Shrink = 0f, AlignSelf = FlexAlign.Stretch, Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
                        Children = source is null ? Array.Empty<Element>() : new[] { LottieView.Create(source, layout.IconColumn, HeroOptions) },
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

    /// <summary>A vertical radio group over <paramref name="selected"/>: the stock <see cref="RadioButtons"/>, or at a hit target (Compact) one 40-DIP radio per choice. The write and
    /// <paramref name="onChange"/> are the stock group's.</summary>
    internal static Element TouchRadios(IReadOnlyList<string> labels, Signal<int> selected, float target, Action<int>? onChange = null, string? header = null)
    {
        if (target <= 0f) return RadioButtons.Create(labels, selected, onChange: onChange, header: header);
        var kids = new List<Element>(labels.Count + 1);
        if (header is { Length: > 0 }) kids.Add(Caption(header) with { Color = Tok.TextSecondary });
        int current = selected.Value;
        for (int i = 0; i < labels.Count; i++)
        {
            int index = i;
            kids.Add(CommonTouch.Lift(RadioButton.Create(labels[i], current == index, () =>
            {
                if (selected.Peek() == index) return;
                selected.Value = index;
                onChange?.Invoke(index);
            }), target));
        }
        return new BoxEl { Direction = 1, Children = kids.ToArray() };
    }

    private static readonly EnterExit BodyEnter = new(Dy: ClassicSetupRules.BodyEnterDy, Opacity: 0f, Active: true);
    private static readonly EnterExit BodyExit = new(Dy: ClassicSetupRules.BodyExitDy, Opacity: 0f, Active: true);
}
