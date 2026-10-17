// ── Themes/Zune/Structure/Views/Screens/ZuneSetup.cs — Zune's setup screens: the plate (themes plan 6.3, P5 Z8b) ────────────────────────────────
//
// Board Z-Setup (and -Compact, -Small, -Huge): a flat plate on the dialog ground, 762 x 490 at Standard, with an ACCENT COLUMN on its left (192 wide) that carries the step as a
// giant numeral in the ink-on-accent at 35 %, the page's name (26/300) and the four steps' names; the page on its right (a 40/300 title over the body, the body scrolling) and
// the footer under it: the step label over a 2-DIP progress line, then back as a word, then next as the round accent button with its word beside it. At Compact the plate fills
// the window under the title row, the accent column becomes a 120-high strip across the top and the footer stacks.
//
//   Setup.Wizard   the plate · Enter / Backspace · Flow.KeepAlive(page; Nav.RecipeFor(Direction.Peek()); no replay on activate)
//   ├ accent      the step numeral (`session.StepNumber()`), the page's name, the steps' names
//   ├ pages       Terms | Sign in | Theme | Local playback: `ZuneSetup.Pages.cs`, `.SignIn.cs`, `.Runtime.cs`
//   └ footer      step label + progress · back · secondary · primary, from `Setup.Commands.ResolveFor` (`session.Row()`)
//
// Every decision is core: `Setup.WizardModel` (page, direction, the footer row, the verbs), `Setup.Gating`, `Setup.SignInRules`, `Setup.RuntimeRules` and the models; this code lays
// out and wires. The tier's record is `ZuneSetupTiers.Of(ctx.Tier())` (Rules\ZuneSetupRules.cs holds every number that differs by window size). The board's heroes (the Lottie
// artwork of the other themes) are not drawn: the accent column is the page's picture.

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

/// <summary>Zune's setup screens.</summary>
internal static partial class ZuneSetup
{
    /// <summary>Registers the seven setup keys (<c>ZuneStructure.RegisterScreens</c> calls it once).</summary>
    public static void Register(ViewRegistryBuilder views)
    {
        views.Add(ViewKeys.SetupWizard, WizardTemplate, "zune.setup.wizard");
        views.Add(ViewKeys.SetupTerms, TermsTemplate, "zune.setup.terms");
        views.Add(ViewKeys.SetupSignIn, SignInPageTemplate, "zune.setup.sign-in");
        views.Add(ViewKeys.SetupTheme, ThemeTemplate, "zune.setup.theme");
        views.Add(ViewKeys.SetupLocalPlayback, LocalPlaybackTemplate, "zune.setup.local-playback");
        views.Add(ViewKeys.SetupSignInDoor, SignInDoorTemplate, "zune.setup.sign-in-door");
        views.Add(ViewKeys.SetupRuntimeCard, RuntimeCardTemplate, "zune.setup.runtime-card");
    }

    // ── the plate ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The registered template of <see cref="ViewKeys.SetupWizard"/>.</summary>
    public static Element WizardTemplate(SetupWizardProps props, in ViewContext ctx)
    {
        var session = props.Session;
        var layout = ZuneSetupTiers.Of(ctx.Tier());   // the window tier's record: final in the first frame, a crossing animates
        Size2 vp = ctx.Hooks.UseContextSignal(Viewport.Size).Value;
        Setup.WizardPage page = session.Page.Value;

        void OnKey(KeyEventArgs e)
        {
            if (e.Handled) return;
            if (e.KeyCode == Keys.Enter && session.Row().PrimaryEnabled) { session.Primary(); e.Handled = true; }
            else if (e.KeyCode == Keys.Back && Setup.Gating.ShowsBack(session.Page.Peek())) { session.Back(); e.Handled = true; }
        }

        // PagesHost claims Grow/Shrink/Min 0 so the keep-alive (a ComponentEl with no layout columns) gets a DEFINITE box and the page's ScrollView scrolls instead of
        // shrink-wrapping and clipping.
        Element pages = new BoxEl
        {
            Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, ClipToBounds = true,
            Children =
            [
                Flow.KeepAlive(() => session.Page.Value, static p => "zsetup:page:" + (int)p, p => PageSlot(props, p),
                    new KeepAliveOptions(MaxEntries: Z.KeepAlivePages, TransitionFor: (_, _) => Design.Nav.RecipeFor(session.Direction.Peek()),
                        SuppressLayoutTransitionsOnActivation: true)),
            ],
        };
        Element content = new BoxEl
        {
            Key = "zsetup:content", Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            Children =
            [
                new BoxEl { Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Padding = layout.ContentPadding, Children = [pages] },
                Embed.Comp(() => new WizardFooter(session)),
            ],
        };
        // The plate (or the Compact sheet): one node in every tier, so a crossing moves and resizes it instead of remounting it.
        Element plate = new BoxEl
        {
            Key = "zsetup:plate", Direction = (byte)(layout.Sheet ? 1 : 0), Grow = 1f, AlignSelf = FlexAlign.Stretch, MinWidth = layout.MinPlateWidth, MinHeight = layout.MinPlateHeight,
            Fill = Tok.FillLayerAlt, Shadow = layout.Sheet ? (ShadowSpec?)null : Elevation.Dialog, ClipToBounds = true, OnKeyDown = OnKey,
            Children = [AccentColumn(session, page, layout), content],
        };
        // The root is the plate's size (or the window's, for the sheet, whose top padding keeps the title row clear and reachable).
        return new BoxEl
        {
            Key = "zsetup:root", Direction = 1, Width = layout.RootWidth(vp.Width), Height = layout.RootHeight(vp.Height),
            MinWidth = layout.MinPlateWidth, MinHeight = layout.MinPlateHeight, Padding = new Edges4(0f, layout.RootTopPad, 0f, 0f),
            HitTestPassThrough = layout.Sheet, Layout = Design.Reveal.Resize,
            Children = [plate],
        };
    }

    /// <summary>The accent column (a strip across the top at Compact): the step as a giant numeral at 35 % of the ink-on-accent, the page's name, the steps' names. ONE node in
    /// every tier; its width, height and the numeral's place are the record's.</summary>
    static Element AccentColumn(Setup.WizardModel session, Setup.WizardPage page, ZuneSetupLayout layout)
    {
        var step = session.StepNumber();
        ColorF ink = Tok.TextOnAccentPrimary;
        var text = new List<Element>(2)
        {
            ZuneText.Cased(R.Type.Heading(Loc.Get(PageName(page))), LabelKind.Heading) with { Color = ink, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f },
            ZuneText.Cased(R.Type.MicroMeta(Loc.Get(Strings.Theme.Zune.Screens.SetupSteps)), LabelKind.Label)
                with { Color = Tok.TextOnAccentSecondary, MaxLines = 2, Trim = TextTrim.WordEllipsis, Wrap = TextWrap.Wrap, MinWidth = 0f },
        };
        return new BoxEl
        {
            Key = "zsetup:accent", ZStack = true, Shrink = 0f, ClipToBounds = true, Fill = R.Ink.AccentAction,
            Width = layout.Sheet ? float.NaN : layout.AccentWidth, Height = layout.Sheet ? layout.AccentStripHeight : float.NaN,
            AlignSelf = FlexAlign.Stretch, Animate = ZuneMotion.ColumnCollapse,
            Children =
            [
                new BoxEl
                {
                    Key = "zsetup:numeral", AlignSelf = FlexAlign.Start, JustifySelf = layout.Sheet ? FlexAlign.End : FlexAlign.Start,
                    Margin = layout.NumeralMargin, HitTestVisible = false,
                    Children =
                    [
                        R.Type.NowPlayingDisplay(step is { } n ? n.Step.ToString(System.Globalization.CultureInfo.InvariantCulture) : "")
                            with { Color = ink with { A = Z.NumeralAlpha }, MaxLines = 1 },
                    ],
                },
                new BoxEl
                {
                    Key = "zsetup:accent-text", Direction = 1, Gap = Z.AccentTextGap, AlignSelf = FlexAlign.End, JustifySelf = FlexAlign.Stretch,
                    Padding = layout.AccentPadding, HitTestVisible = false, Children = text.ToArray(),
                },
            ],
        };
    }

    /// <summary>The page's name in the accent column (a short word: the title beside it says the rest).</summary>
    static string PageName(Setup.WizardPage page) => page switch
    {
        Setup.WizardPage.Terms => Strings.Theme.Zune.Screens.SetupStepTerms,
        Setup.WizardPage.SignIn => Strings.Theme.Zune.Screens.SetupStepSignIn,
        Setup.WizardPage.Theme => Strings.Theme.Zune.Screens.SetupStepTheme,
        _ => Strings.Theme.Zune.Screens.SetupStepPlayback,
    };

    static Element PageSlot(SetupWizardProps props, Setup.WizardPage page) => new BoxEl
    {
        Key = "zsetup:capture:" + (int)page, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1,
        Children =
        [
            page switch
            {
                Setup.WizardPage.Terms => Slots.View(ViewKeys.SetupTerms, props.Terms) with { Key = "zsetup:page:terms" },
                Setup.WizardPage.SignIn => Slots.View(ViewKeys.SetupSignIn, props.SignIn) with { Key = "zsetup:page:sign-in" },
                Setup.WizardPage.Theme => Slots.View(ViewKeys.SetupTheme, props.Theme) with { Key = "zsetup:page:theme" },
                _ => Slots.View(ViewKeys.SetupLocalPlayback, props.LocalPlayback) with { Key = "zsetup:page:local-playback" },
            },
        ],
    };

    // ── the footer ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The footer: the step label over the progress line on the left, back as a word, the secondary as a word and the primary as the round accent button with its word
    /// beside it on the right; a column at Compact.</summary>
    private sealed class WizardFooter(Setup.WizardModel session) : CommonTierComponent
    {
        public override Element Render()
        {
            var layout = ZuneSetupTiers.Of(ReadTier());
            var k = new ZuneKit(ZuneSettingsTiers.For(layout.Tier));
            Size2 vp = UseContextSignal(Viewport.Size).Value;
            Setup.CommandRow row = session.Row();
            var page = session.Page.Value;

            string primary = Loc.Get(row.PrimaryKey ?? Strings.Setup.Accept);
            var actions = new List<Element>(3);
            if (Setup.Gating.ShowsBack(page))
                actions.Add(k.Cmd(Loc.Get(Strings.Theme.Zune.Screens.SetupBack), session.Back) with { Key = "zsetup:footer:back" });
            if (row.SecondaryKey is { } secondary)
                actions.Add(k.Cmd(Loc.Get(secondary), session.Secondary, row.SecondaryEnabled) with { Key = "zsetup:footer:secondary" });
            actions.Add((row.PrimaryKind == Setup.ButtonKind.Standard
                ? k.Cmd(primary, session.Primary, row.PrimaryEnabled, accent: true)
                : k.Go(primary, session.Primary, row.PrimaryEnabled)) with { Key = "zsetup:footer:primary" });

            var step = session.StepNumber();
            Element progress = new BoxEl
            {
                Direction = 1, Gap = Z.ProgressGap, Shrink = 0f, MinWidth = 0f, AlignItems = FlexAlign.Start,
                Width = layout.FooterStacked ? float.NaN : layout.ProgressColumn,
                AlignSelf = layout.FooterStacked ? FlexAlign.Stretch : FlexAlign.Center,
                Children =
                [
                    ZuneText.Meta(step is { } n ? Strings.Setup.StepOf(n.Step, n.Total) : Loc.Get(Strings.Setup.PreSetup)),
                    ProgressBar.Determinate(session.Progress(), layout.ProgressWidth(vp.Width)),
                ],
            };
            return new BoxEl
            {
                Key = "zsetup:footer", Shrink = 0f, MinWidth = 0f, Padding = layout.FooterPadding, Gap = Z.FooterGap,
                Direction = (byte)(layout.FooterStacked ? 1 : 0), AlignItems = layout.FooterStacked ? FlexAlign.Stretch : FlexAlign.Center,
                Children =
                [
                    progress,
                    new BoxEl
                    {
                        Grow = 1f, Direction = 0, Justify = layout.FooterStacked ? FlexJustify.SpaceBetween : FlexJustify.End, AlignItems = FlexAlign.Center,
                        Gap = Z.FooterActionGap, MinWidth = 0f, Wrap = true, Children = actions.ToArray(),
                    },
                ],
            };
        }
    }

    // ── the page frame (title + scrolling body) ─────────────────────────────────────────────────────────────────────

    /// <summary>A page's frame: the 40/300 title over the scrolling body. The outer box claims the parent's Grow / Shrink / Min so the ScrollView is bounded.</summary>
    internal static Element WizardFrame(Setup.WizardPage page, string header, Element body) => new BoxEl
    {
        Key = "zsetup:frame:" + (int)page, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Direction = 1, Gap = Z.TitleGap,
        Children =
        [
            ZuneText.Title(header) with { MaxLines = 2, Trim = TextTrim.WordEllipsis, Shrink = 0f },
            ScrollView(body) with { Grow = 1f, Shrink = 1f, MinHeight = 0f, MinWidth = 0f, EdgeCues = ScrollEdgeCues.None, AlwaysShowScrollbar = true },
        ],
    };

    // ── the page body vocabulary ────────────────────────────────────────────────────────────────────────────────────

    internal static class SetupText
    {
        public static Element Stack(params Element[] kids) => new BoxEl { Direction = 1, Gap = Z.BodyGap, MinWidth = 0f, Children = kids };
        public static Element Group(params Element[] kids) => new BoxEl { Direction = 1, Gap = Z.BodyInnerGap, MinWidth = 0f, Children = kids };
        /// <summary>The lead sentence: 15/20 in the ink, wrapping.</summary>
        public static Element Lead(string s) => R.Type.TrackTitle(s) with { Wrap = TextWrap.Wrap, MinWidth = 0f };
        /// <summary>Body copy: 14/18 in the secondary ink, wrapping.</summary>
        public static Element Body(string s) => R.Type.DenseTitle(s) with { Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f };
        /// <summary>Fine print: the micro voice in the tertiary ink, wrapping.</summary>
        public static Element Secondary(string s) => R.Type.MicroMeta(s) with { Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MinWidth = 0f };

        /// <summary>A panel: the raised ground, a 15/20 header, a 12 line under it, an optional glyph and an optional control (the board's card on the dialog).</summary>
        public static Element Card(string header, string? description, string? glyph = null, Element? content = null, Action? onClick = null) => new BoxEl
        {
            Direction = 0, Gap = Z.CardGap, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch, MinWidth = 0f, MinHeight = Z.CardMinHeight,
            Padding = Edges4.All(Z.CardPad), Fill = Tok.FillSolidTertiary,
            Role = onClick is null ? default : AutomationRole.Button, Focusable = onClick is not null, Cursor = onClick is null ? default : CursorId.Hand, OnClick = onClick,
            Children = Cells(header, description, glyph, content),
        };

        static Element[] Cells(string header, string? description, string? glyph, Element? content)
        {
            var kids = new List<Element>(3);
            if (glyph is not null) kids.Add(Icon(glyph, Z.CardGlyph, Tok.TextSecondary) with { Shrink = 0f });
            var words = new List<Element>(2) { ZuneText.Label(header) };
            if (description is { Length: > 0 }) words.Add(ZuneText.Hint(description));
            kids.Add(new BoxEl { Direction = 1, Gap = Z.CardWordsGap, Grow = 1f, Basis = 0f, MinWidth = 0f, Children = words.ToArray() });
            if (content is not null) kids.Add(content);
            return kids.ToArray();
        }
    }

    /// <summary>A vertical radio group over <paramref name="selected"/>: the stock <see cref="RadioButtons"/>, or at a hit target (Compact) one 40-DIP radio per choice.</summary>
    internal static Element TouchRadios(IReadOnlyList<string> labels, Signal<int> selected, float target, Action<int>? onChange = null, string? header = null)
    {
        if (target <= 0f) return RadioButtons.Create(labels, selected, onChange: onChange, header: header);
        var kids = new List<Element>(labels.Count + 1);
        if (header is { Length: > 0 }) kids.Add(ZuneText.Hint(header));
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

    private static readonly EnterExit BodyEnter = new(Dy: Z.BodyEnterDy, Opacity: 0f, Active: true);
    private static readonly EnterExit BodyExit = new(Dy: Z.BodyExitDy, Opacity: 0f, Active: true);
}
