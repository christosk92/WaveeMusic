using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The setup wizard's RIGHT (480-DIP) column primitives — bigger, standalone pieces than
/// <see cref="SetupCompact"/>'s dense rows: a clickable option (sign-in's browser/scan choice), a numbered progress
/// step (local playback's download/verify/ready ladder, the Done checklist), a pill chip, and the column shell that
/// pins a footnote to the floor.</summary>
static class SetupDecision
{
    /// <summary>A big clickable choice — sign-in's "Open in your browser" vs "Scan a QR code". 28-px round avatar +
    /// title/sub, optionally a <paramref name="trailing"/> badge beside the title (the "Recommended" pill — a caller
    /// concern, not baked in here, so this stays reusable for a plain unadorned choice too).
    /// <paramref name="recommended"/> draws the WaveePicker.Card inward-growing border trick: the resting padding
    /// spends 1 DIP on the 1→2 border growth, so the border draws INWARD and the card's content never shifts by a
    /// pixel when it lights up. <paramref name="onClick"/> = null degrades to a plain (non-interactive,
    /// <see cref="AutomationRole.None"/>) info card.</summary>
    public static Element OptionCard(string title, string sub, ColorF avatarFill, string glyph, ColorF ink,
        bool recommended, Action? onClick, Element? trailing = null)
    {
        Element avatar = new BoxEl
        {
            Width = 28f, Height = 28f, Shrink = 0f,
            Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
            Corners = Radii.Circle(28f), Fill = avatarFill,
            Children = [Icon(glyph, 14f, ink)],
        };

        Element titleRow = trailing is null
            ? new TextEl(title) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }
            : new BoxEl
            {
                Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, MinWidth = 0f,
                Children =
                [
                    new BoxEl
                    {
                        Shrink = 0f,
                        Children = [new TextEl(title) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }],
                    },
                    trailing,
                ],
            };

        return new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, Shrink = 0f, AlignSelf = FlexAlign.Stretch,
            Padding = Edges4.All(recommended ? 11f : 12f),
            Corners = Radii.CardAll,
            Fill = Tok.FillCardDefault, HoverFill = onClick is null ? Tok.FillCardDefault : Tok.FillCardSecondary,
            BorderWidth = recommended ? 2f : 1f, BorderColor = recommended ? Tok.AccentDefault : Tok.StrokeCardDefault,
            Role = onClick is null ? AutomationRole.None : AutomationRole.Button,
            Focusable = onClick is not null, Cursor = onClick is not null ? CursorId.Hand : CursorId.Arrow,
            OnClick = onClick,
            Children =
            [
                avatar,
                new BoxEl
                {
                    Direction = 1, Gap = 2f, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        titleRow,
                        new TextEl(sub) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.WordEllipsis },
                    ],
                },
            ],
        };
    }

    /// <summary>One numbered progress step (local playback's download/verify/ready ladder; the Done checklist). The
    /// 24-px badge tells the state at a glance: <see cref="SetupStepState.Pending"/> shows its own ordinal on an
    /// accent-subtle disc, <see cref="SetupStepState.Current"/> an indeterminate ring, <see cref="SetupStepState.Done"/>
    /// a check, <see cref="SetupStepState.Attention"/>/<see cref="SetupStepState.Failed"/> a caution glyph. Height is
    /// content-driven (not fixed) — a one-line <paramref name="body"/> lands around 52 DIP, a two-line one around 68;
    /// callers budget against those two numbers rather than this method taking a Height it can't itself decide (the
    /// caller knows how many lines its own body text will need).</summary>
    public static Element StepCard(int n, SetupStepState state, string title, string body)
    {
        Element badge = state switch
        {
            SetupStepState.Current => new BoxEl
            {
                Width = 24f, Height = 24f, Shrink = 0f, Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
                Children = [ProgressRing.Indeterminate(16f)],
            },
            SetupStepState.Done => new BoxEl
            {
                Width = 24f, Height = 24f, Shrink = 0f, Corners = Radii.Circle(24f), Fill = Tok.AccentDefault,
                Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
                Children = [Icon(Icons.Accept, 12f, Tok.TextOnAccentPrimary)],
            },
            SetupStepState.Attention or SetupStepState.Failed => new BoxEl
            {
                Width = 24f, Height = 24f, Shrink = 0f, Corners = Radii.Circle(24f),
                Fill = Tok.SystemFillCaution with { A = 0.16f },
                Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
                Children = [Icon(Icons.StatusWarning, 12f, Tok.SystemFillCaution)],
            },
            _ => new BoxEl
            {
                Width = 24f, Height = 24f, Shrink = 0f, Corners = Radii.Circle(24f), Fill = Tok.AccentSubtle,
                Justify = FlexJustify.Center, AlignItems = FlexAlign.Center,
                Children = [new TextEl(n.ToString()) { Size = 12f, Weight = 600, Color = Tok.AccentTextPrimary }],
            },
        };

        return new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Start, Shrink = 0f, AlignSelf = FlexAlign.Stretch,
            Padding = new Edges4(Spacing.M, Spacing.S, Spacing.M, Spacing.S),   // 20 + 32 + 16 = 68 with a 2-line body
            Corners = Radii.CardAll, Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                badge,
                new BoxEl
                {
                    Direction = 1, Gap = 2f, Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        new TextEl(title) { Size = 14f, LineHeight = 20f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                        new TextEl(body) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.WordEllipsis },
                    ],
                },
            ],
        };
    }

    /// <summary>A 32-DIP pill button with a leading glyph — <c>LoginView.OpenButton</c>'s chrome, generalized
    /// ("Choose a Spotify.dll…", "Use installed Spotify").</summary>
    public static BoxEl Chip(string glyph, string label, Action onClick) => new BoxEl
    {
        Direction = 0, Gap = Spacing.XS, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Height = 32f, Shrink = 0f, Padding = new Edges4(10f, 0f, 12f, 0f),
        Corners = Radii.ControlAll,
        Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
        BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault, BrushTransitionMs = Motion.ControlFaster,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
        Children =
        [
            Icon(glyph, 14f, Tok.TextSecondary),
            new TextEl(label) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary },
        ],
    };

    /// <summary>A wrapping row of <see cref="Chip"/>s — local playback's "Choose a folder / Use installed / Choose a
    /// version" trio.</summary>
    public static BoxEl ChipRow(params Element[] chips) => new BoxEl
    {
        Direction = 0, Gap = Spacing.S, Wrap = true, Shrink = 0f, AlignItems = FlexAlign.Center,
        Children = chips,
    };

    /// <summary>The decision column shell. <paramref name="wide"/> reserves the full
    /// <see cref="SetupLayout.DecisionBodyBudget"/> as a <c>MinHeight</c> (so a short column — Sign-in's "Done" phase
    /// — still fills the lane instead of collapsing to its content and leaving the stage looking taller); pass false
    /// for a column that's fine sizing to its own content (a Compact-tier fallback with no fixed lane to fill).
    /// <paramref name="pinnedBottom"/>, when given, is appended after a <see cref="SetupCompact.Spacer"/> so it lands
    /// on the floor of the column exactly like the stage's own <see cref="SetupStage.Caption"/> does.</summary>
    public static Element Column(bool wide, IReadOnlyList<Element> kids, Element? pinnedBottom = null, int leadLines = 1)
    {
        var children = new List<Element>(kids.Count + 2);
        children.AddRange(kids);
        if (pinnedBottom is not null)
        {
            children.Add(SetupCompact.Spacer());
            children.Add(pinnedBottom);
        }
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.S, AlignItems = FlexAlign.Stretch, Shrink = 0f, MinWidth = 0f,
            MinHeight = wide ? SetupLayout.DecisionBodyBudget(leadLines) : 0f,
            Children = children.ToArray(),
        };
    }
}
