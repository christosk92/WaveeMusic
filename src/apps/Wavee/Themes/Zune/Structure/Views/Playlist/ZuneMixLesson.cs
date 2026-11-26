// ── Themes/Zune/Structure/Views/Playlist/ZuneMixLesson.cs — Zune's Mix lesson page, "build a transition together" (#48 Mix) ─────────────
//
// Zune's own voice for the lesson (Themes/Common/CommonMixLesson.cs): everything flat on the ground. The editor is Zune's own Edit transition
// in its teaching mode (its lowercase heading, a flat square chip, square thumbs and beacons); the guide's title is the page hero in large
// lowercase type (never shrunk: it wraps), commands are lowercase words (the primary in the accent), the comparison two words, the notes a glyph
// and a line, the steps square nodes on a thin line with the open step's name in the module header's voice, the tasks flat rows.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using Wavee.Themes.Common;
using static FluentGpu.Dsl.Ui;
using E = Wavee.Themes.Zune.ZuneMixEditorRules;

namespace Wavee.Themes.Zune;

internal static class ZuneMixLesson
{
    /// <summary>The registered template of <see cref="ViewKeys.MixLesson"/>.</summary>
    public static Element Template(MixLessonProps p, in ViewContext ctx)
    {
        var tier = ctx.Tier();
        // The lesson's commands are the touch target at every tier (a crossing never resizes them).
        float target = E.CompactTarget;
        return CommonMixLesson.Page(p, Voice(tier.Window, target), E.LessonStacked(tier.Content), in ctx);
    }

    static MixLessonVoice Voice(WidthTier window, float target) => new()
    {
        Gutter = ZuneTiers.Frame(window).Gutter, ColumnGap = E.LessonColumnGap, GuideWidth = window >= WidthTier.Huge ? E.LessonGuideWide : E.LessonGuide,
        Top = E.Gap, Gap = E.LessonGap, HeroGap = E.LessonHeroGap, Swap = ZuneMotion.SwapFade, SwapMotion = ZuneMotion.SwapThroughMotion,
        Eyebrow = static s => R.Type.MicroMeta(s) with { Color = Tok.TextSecondary, Case = R.Case.For(LabelKind.Label) },
        HeroTitle = static s => R.Type.PageHero(s) with { Case = R.Case.For(LabelKind.Heading), Color = Tok.TextPrimary },
        Lead = static s => BodyLarge(s) with { Color = Tok.TextSecondary },
        Body = static s => Body(s) with { Color = Tok.TextSecondary },
        Strong = static s => R.Type.DenseTitle(s) with { Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Label) },
        Caption = static s => R.Type.DenseMeta(s) with { Color = Tok.TextTertiary, Case = R.Case.For(LabelKind.Label) },
        Button = (key, label, act, accent, glyph, enabled) => ZuneMixEditor.Word(key, label, enabled ? act : null, target, primary: accent, glyph: glyph),
        GlyphButton = (key, glyph, act, name, enabled) => ToolTip.Wrap(Controls.IconAction(glyph, act, size: target, isEnabled: enabled) with { Key = key }, name),
        Choice = (key, a, b, sel, pick, enabled) => new BoxEl
        {
            Key = key, Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f,
            Children =
            [
                Choice(key + ":0", a, sel == 0, enabled ? () => pick(0) : null, target),
                Choice(key + ":1", b, sel == 1, enabled ? () => pick(1) : null, target),
            ],
        },
        Note = static (success, rich) => Note(success, rich),
        Stepper = new Stepper.Style
        {
            NodeCorner = 0f, Accent = Tok.AccentDefault, LineColor = Tok.TextPrimary with { A = E.LessonLineAlpha }, NodeFill = ColorF.Transparent,
            MarkerInk = Tok.TextSecondary, DoneInk = Tok.TextOnAccentPrimary, MarkerSize = E.LessonNodeMarker, HeadCorner = 0f,
            HeadHover = R.Layer.RowHover, HeadPressed = R.Layer.RowPressed,
            Title = static (t, current) => (current ? R.Type.ModuleHeader(t) : R.Type.DenseTitle(t)) with { Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Heading) },
            Subtitle = static (t, current) => R.Type.DenseMeta(t) with { Color = current ? Tok.TextSecondary : Tok.TextTertiary, Case = R.Case.For(LabelKind.Label) },
        },
        TaskCorner = 0f, TaskNow = Tok.TextPrimary with { A = E.LessonNowAlpha }, CheckSize = E.LessonCheck, Square = 0f, Accent = Tok.AccentDefault,
        Rule = Tok.TextPrimary with { A = E.LessonLineAlpha }, Track = Tok.TextPrimary with { A = E.LessonTrackAlpha },
    };

    /// <summary>One of the two versions as a word: the one that plays in the primary ink, the other receded.</summary>
    static Element Choice(string key, string label, bool on, Action? click, float target)
    {
        bool live = click is not null;
        var ink = !live ? Tok.TextDisabled : on ? Tok.TextPrimary : Tok.TextTertiary;
        return new BoxEl
        {
            Key = key, Direction = 0, AlignItems = FlexAlign.Center, Height = target, Shrink = 0f, Padding = new Edges4(E.WordPadX, 0f, E.WordPadX, 0f),
            HoverFill = live ? R.Layer.RowHover : ColorF.Transparent, PressedFill = live ? R.Layer.RowPressed : ColorF.Transparent,
            Role = AutomationRole.RadioButton, Focusable = live, IsEnabled = live, Cursor = live ? CursorId.Hand : null, OnClick = click,
            Children = [R.Type.DenseTitle(label) with { Color = ink, Case = R.Case.For(LabelKind.Command), MaxLines = 1, Wrap = TextWrap.NoWrap, BrushTransitionMs = CommonMixLessonRules.BrushMs }],
        };
    }

    /// <summary>A note, flat: its glyph in the accent (done: the success ink) and its line (a leading <c>**…**</c> in the primary ink).</summary>
    static Element Note(bool success, string rich)
    {
        string words = rich.Replace("**", "", StringComparison.Ordinal).Replace("[[", "", StringComparison.Ordinal).Replace("]]", "", StringComparison.Ordinal);
        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Start, Gap = E.RowGap, MinWidth = 0f,
            Children =
            [
                Icon(success ? Icons.Accept : Icons.Info, E.Glyph, success ? Tok.SystemFillSuccess : Tok.AccentDefault),
                Body(words) with { Color = success ? Tok.TextPrimary : Tok.TextSecondary, Wrap = TextWrap.Wrap, Shrink = 1f, MinWidth = 0f },
            ],
        };
    }
}
