// ── Themes/Common/CommonMixLessonFluent.cs — the Fluent family's voice for the Mix lesson (WinUI and Classic, #48 Mix) ────────────────
//
// WinUI and Classic are one family for the lesson (owner, 2026-11-26: "classic = winui"): stock Fluent controls on the window's material.
// The guide's buttons are Button / AccentButton (the large size for the hero's), the comparison a SegmentedControl, the notes InfoBars, the
// steps the stepper with round nodes, the tasks a card of list rows carrying the selection pill. Each theme passes its own card plate,
// corner and gutters (`FluentLessonParts`), so Classic keeps its control layer and its radii.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;
using Wavee.Theming.Contracts;
using L = Wavee.Themes.Common.CommonMixLessonRules;

namespace Wavee.Themes.Common;

/// <summary>What a Fluent theme brings to the lesson: its gutters, the guide's width beside the editor, its card and its corner, its
/// stepper's line, the current task's plate and the editor's plate in the teaching mode.</summary>
public sealed record FluentLessonParts(float Gutter, float ColumnGap, float GuideWidth, float Top, ColorF Card, ColorF CardStroke, float Corner,
    ColorF TaskNow, ColorF Line, ColorF Track, EnterExit Swap, FluentGpu.Animation.MotionTokenDef SwapMotion);

public static class CommonMixLessonFluent
{
    /// <summary>The guide's voice in Fluent controls.</summary>
    public static MixLessonVoice Voice(FluentLessonParts parts) => new()
    {
        Swap = parts.Swap, SwapMotion = parts.SwapMotion,
        Gutter = parts.Gutter, ColumnGap = parts.ColumnGap, GuideWidth = parts.GuideWidth, Top = parts.Top, Gap = FluentLessonRules.Gap,
        HeroGap = FluentLessonRules.HeroGap,
        Eyebrow = static s => Ui.Caption(s) with { Color = Tok.TextSecondary },
        HeroTitle = static s => Ui.TitleLarge(s),
        Lead = static s => Ui.BodyLarge(s) with { Color = Tok.TextSecondary },
        Body = static s => Ui.Body(s) with { Color = Tok.TextSecondary },
        Strong = static s => Ui.BodyStrong(s),
        Caption = static s => Ui.Caption(s) with { Color = Tok.TextTertiary },
        Button = static (key, label, act, accent, glyph, enabled) => Button.Create(label, act, accent ? ButtonAppearance.Accent : ButtonAppearance.Standard,
            ControlSize.Large, glyph, isEnabled: enabled) with { Key = key },
        GlyphButton = static (key, glyph, act, name, enabled) => ToolTip.Wrap(IconButton.Create(glyph, act, isEnabled: enabled) with
            { Key = key, MinWidth = FluentLessonRules.Target, MinHeight = FluentLessonRules.Target }, name),
        Choice = static (key, a, b, sel, pick, enabled) => Choice(key, a, b, sel, pick, enabled),
        Note = static (success, rich) =>
        {
            var (title, message) = Split(rich);
            return InfoBar.Create(success ? InfoBarSeverity.Success : InfoBarSeverity.Informational, title, message, isClosable: false);
        },
        Stepper = new Stepper.Style
        {
            Accent = Tok.AccentDefault, LineColor = parts.Line, NodeFill = ColorF.Transparent, MarkerInk = Tok.TextTertiary, DoneInk = Tok.TextOnAccentPrimary,
            Title = static (t, current) => current ? Ui.Subtitle(t) : Ui.BodyStrong(t),
            Subtitle = static (t, current) => Ui.Caption(t) with { Color = current ? Tok.TextSecondary : Tok.TextTertiary },
            HeadCorner = parts.Corner * 0.5f,
        },
        TaskCard = parts.Card, TaskCardStroke = parts.CardStroke, TaskCorner = parts.Corner, TaskNow = parts.TaskNow, CheckSize = FluentLessonRules.Check,
        Accent = Tok.AccentDefault, Rule = Tok.StrokeDividerDefault, Track = parts.Track,
    };

    /// <summary>Two versions as WinUI's SegmentedControl: a recessed plate, each choice a button, the one that plays raised with the accent pill
    /// under its word (no choice raised before the first play).</summary>
    static Element Choice(string key, string a, string b, int sel, Action<int> pick, bool enabled)
    {
        Element Item(int i, string label)
        {
            bool on = sel == i;
            var kids = new List<Element>(2)
            {
                Ui.Body(label) with { Color = !enabled ? Tok.TextDisabled : on ? Tok.TextPrimary : Tok.TextSecondary, MaxLines = 1, Wrap = TextWrap.NoWrap,
                    JustifySelf = FlexAlign.Center, AlignSelf = FlexAlign.Center },
            };
            if (on)
                kids.Add(new BoxEl
                {
                    JustifySelf = FlexAlign.Center, AlignSelf = FlexAlign.End, Width = FluentLessonRules.PillW, Height = FluentLessonRules.PillH,
                    Margin = new Edges4(0f, 0f, 0f, FluentLessonRules.PillBottom), Corners = CornerRadius4.All(FluentLessonRules.PillH * 0.5f),
                    Fill = Tok.AccentDefault, HitTestVisible = false,
                });
            ColorF rest = on ? Tok.FillControlDefault : ColorF.Transparent;
            return new BoxEl
            {
                Key = key + ":" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), ZStack = true, Shrink = 0f, AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center, Padding = new Edges4(FluentLessonRules.SegPadX, 0f, FluentLessonRules.SegPadX, 0f), MinHeight = FluentLessonRules.SegH,
                Corners = Radii.ControlAll, Fill = rest, HoverFill = enabled && !on ? Tok.FillSubtleSecondary : rest,
                PressedFill = enabled && !on ? Tok.FillSubtleTertiary : rest, BrushTransitionMs = CommonMixLessonRules.BrushMs,
                Role = AutomationRole.RadioButton, Focusable = enabled, IsEnabled = enabled, Cursor = enabled ? CursorId.Hand : null,
                OnClick = enabled ? () => pick(i) : null, Children = kids.ToArray(),
            };
        }
        return new BoxEl
        {
            Key = key, Direction = 0, Gap = FluentLessonRules.SegGap, Shrink = 0f, Padding = Edges4.All(FluentLessonRules.SegGap), Corners = Radii.ControlAll,
            Fill = Tok.FillControlAltSecondary, BorderColor = Tok.StrokeControlDefault, BorderWidth = 1f,
            Children = [Item(0, a), Item(1, b)],
        };
    }

    /// <summary>A note's words as an InfoBar's title (a leading <c>**…**</c>) and message (the rest, its marks dropped).</summary>
    static (string Title, string Message) Split(string rich)
    {
        string title = "", rest = rich;
        if (rich.StartsWith("**", StringComparison.Ordinal) && rich.IndexOf("**", 2, StringComparison.Ordinal) is var end and > 2)
        {
            title = rich[2..end];
            rest = rich[(end + 2)..].TrimStart();
        }
        return (title, rest.Replace("**", "", StringComparison.Ordinal).Replace("[[", "", StringComparison.Ordinal).Replace("]]", "", StringComparison.Ordinal));
    }
}

/// <summary>The Fluent voice's own measures.</summary>
public static class FluentLessonRules
{
    public const float Gap = 14f, HeroGap = 16f, Target = 32f, Check = 20f;
    /// <summary>The comparison's segmented control: a choice's height and side padding, the gap and plate padding, the selection pill under the word.</summary>
    public const float SegH = 28f, SegPadX = 12f, SegGap = 2f, PillW = 16f, PillH = 3f, PillBottom = 1f;
}
