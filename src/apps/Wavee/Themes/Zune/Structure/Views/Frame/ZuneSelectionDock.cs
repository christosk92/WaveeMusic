// ── Themes/Zune/Structure/Views/Frame/ZuneSelectionDock.cs — Zune's docked selection bar (P5 Zune fidelity round 2; Z-State-Rows, -Compact) ────────
//
// The frame slot between the page row and the player bar. A track table in selection mode publishes its bar's model to the `SelectionDockHost` the frame
// provides (contract 1.47); this draws it: "3 selected" in the 22 light display voice, the table's verbs as 32 rings with their lowercase words (play in the
// accent disc), the selection's "..." (the core's own button), then select all and done at the right end. Small keeps the rings and drops the words (they
// are tooltips); Compact is two rows, a header (count · select all · done) over ring columns with the word under each ring, every target 40 or more.
//
// The slot's height IS the reservation: while a selection shows the bar the page row above is that much shorter (an animated reflow, so the last rows are
// never covered and nothing jumps). The bar keeps its content while the height eases back to 0, so it does not vanish mid-slide. Numbers:
// `Rules\ZuneSelectionDockRules.cs`.

using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using FluentGpu.Theming;
using Wavee.Theming;
using Wavee.Theming.Contracts;
using Wavee.Theming.Roles;
using static FluentGpu.Dsl.Ui;
using Rules = Wavee.Themes.Zune.ZuneSelectionDockRules;

namespace Wavee.Themes.Zune;

/// <summary>The frame's docked selection bar slot.</summary>
internal static class ZuneSelectionDock
{
    /// <summary>The slot for <paramref name="host"/>: height 0 until a table publishes a model.</summary>
    public static Element Slot(SelectionDockHost host) => Embed.Comp(host, static () => new DockSlot());

    sealed class DockSlot : Component
    {
        SelectionDockProps? _last;

        public override Element Render()
        {
            var host = UseProps<SelectionDockHost>();
            var model = host.Model.Value;
            if (model is not null) _last = model;
            var facts = TierContext.DrawnIn(UseContext(ThemeFactsContext.Signal)).Value.Tiers;
            WidthTier tier = facts.Policy is null ? WidthTier.Standard : facts.Window;
            float gutter = ZuneTiers.FrameOf(in facts).Gutter;
            float height = model is null ? 0f : Rules.HeightFor(tier);
            return new BoxEl
            {
                Key = Rules.SlotKey, Direction = 1, Shrink = 0f, MinWidth = 0f, ClipToBounds = true,
                Height = height, HitTestVisible = model is not null,
                Animate = Design.Reduced ? null : Rules.HeightMotion,
                Children = [_last is { } shown ? Bar(shown, tier, gutter) : new BoxEl()],
            };
        }
    }

    // ── the bar ──────────────────────────────────────────────────────────────────────────────────────────────────────────

    static Element Bar(SelectionDockProps m, WidthTier tier, float gutter)
        => tier == WidthTier.Compact ? CompactBar(m, gutter) : WideBar(m, tier, gutter);

    static TextEl Count(SelectionDockProps m) => R.Type.Numeral(m.CountLabel) with
    {
        Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Heading), MaxLines = 1, Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
        MinWidth = 0f, Shrink = 0f,
    };

    static Element WideBar(SelectionDockProps m, WidthTier tier, float gutter)
    {
        bool words = Rules.Labels(tier);
        var kids = new List<Element>(m.Commands.Count + 6) { Count(m) };
        foreach (var c in m.Commands) kids.Add(Verb(c, words));
        if (m.More is { } more) kids.Add(new BoxEl { Width = Rules.TextTarget, Height = Rules.TextTarget, Shrink = 0f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [more] });
        kids.Add(new BoxEl { Grow = 1f, MinWidth = 0f });
        kids.Add(TextCommand("dock:select-all", m.SelectAllLabel, m.SelectAll));
        kids.Add(TextCommand("dock:done", m.DoneLabel, m.Done));
        return new BoxEl
        {
            Key = Rules.BarKey, Direction = 0, AlignItems = FlexAlign.Center, Gap = Rules.VerbGap, MinWidth = 0f, Grow = 1f,
            Padding = new Edges4(gutter, 0f, gutter, 0f), Fill = Prop.Of(static () => R.Layer.FloatingPane),
            Children = kids.ToArray(),
        };
    }

    static Element CompactBar(SelectionDockProps m, float gutter)
    {
        var columns = new List<Element>(m.Commands.Count + 1);
        foreach (var c in m.Commands) columns.Add(Column(c));
        if (m.More is { } more)
            columns.Add(new BoxEl
            {
                Key = "dock:more", Direction = 1, Grow = 1f, Basis = 0f, MinWidth = Rules.TextTarget, MinHeight = Rules.ColumnHeight, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Children = [new BoxEl { Width = Rules.TextTarget, Height = Rules.TextTarget, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Children = [more] }],
            });
        return new BoxEl
        {
            Key = Rules.BarKey, Direction = 1, MinWidth = 0f, Grow = 1f, Fill = Prop.Of(static () => R.Layer.FloatingPane),
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Height = Rules.CompactHeader, Shrink = 0f, MinWidth = 0f, Gap = Rules.CountGap,
                    Padding = new Edges4(gutter, 0f, gutter, 0f),
                    Children =
                    [
                        Count(m),
                        new BoxEl { Grow = 1f, MinWidth = 0f },
                        TextCommand("dock:select-all", m.SelectAllLabel, m.SelectAll),
                        TextCommand("dock:done", m.DoneLabel, m.Done),
                    ],
                },
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Height = Rules.CompactColumns, Shrink = 0f, MinWidth = 0f,
                    Padding = new Edges4(gutter / 2f, 0f, gutter / 2f, 0f), Children = columns.ToArray(),
                },
            ],
        };
    }

    // ── the verbs ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A ring disc: the accent filled one for the primary verb, the 2-stroke ring otherwise; the glyph in the verb's own face.</summary>
    static BoxEl Disc(SelectionDockCommand c, float size) => new()
    {
        Width = size, Height = size, Shrink = 0f, Corners = CornerRadius4.All(size / 2f),
        Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Fill = c.Primary ? Prop.Of(static () => R.Ink.AccentAction) : ColorF.Transparent,
        BorderWidth = c.Primary ? 0f : ZuneHomeRules.RingStroke, BorderColor = Tok.TextPrimary,
        HoverFill = c.Primary ? ColorF.Transparent : R.Layer.ChromeHover, PressedFill = c.Primary ? ColorF.Transparent : R.Layer.ChromePressed,
        Children =
        [
            new TextEl(c.Primary && c.Glyph == Icons.Play ? ZunePlayerLayout.PlaySolid : c.Glyph)
            {
                Size = ZuneHomeRules.RingGlyph, FontFamily = c.Font ?? Tok.Definition.Fonts.Icon,
                Color = c.Primary ? Prop.Of(static () => ColorContrast.PickContrast(R.Ink.AccentAction)) : Tok.TextPrimary,
            },
        ],
    };

    /// <summary>The wide bar's verb: the disc and its lowercase word (the word is a tooltip where the tier has no room for it).</summary>
    static Element Verb(SelectionDockCommand c, bool word)
    {
        var kids = new List<Element>(2) { Disc(c, Rules.Ring) };
        if (word)
            kids.Add(R.Type.TrackTitle(c.Label) with { Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Command), MaxLines = 1, Wrap = TextWrap.NoWrap, Shrink = 0f });
        Element box = new BoxEl
        {
            Key = "dock:" + c.Key, Direction = 0, AlignItems = FlexAlign.Center, Gap = ZuneHomeRules.DaylistActionLabelGap, Shrink = 0f,
            MinHeight = Rules.TextTarget, MinWidth = Rules.TextTarget, Justify = FlexJustify.Center,
            Opacity = c.Enabled ? 1f : Rules.DisabledOpacity, IsEnabled = c.Enabled,
            Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = c.Enabled, AllowFocusOnInteraction = false, BlocksDragArm = true,
            OnClick = c.Invoke, Children = kids.ToArray(),
        };
        return ZuneDetailKit.StableNamed(box, c.Label, word);
    }

    /// <summary>The Compact column: the 36 disc over its word, the whole column the target.</summary>
    static Element Column(SelectionDockCommand c) => new BoxEl
    {
        Key = "dock:" + c.Key, Direction = 1, Grow = 1f, Basis = 0f, MinWidth = Rules.TextTarget, MinHeight = Rules.ColumnHeight, Shrink = 1f,
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Gap = Rules.ColumnGap,
        Opacity = c.Enabled ? 1f : Rules.DisabledOpacity, IsEnabled = c.Enabled,
        Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = c.Enabled, AllowFocusOnInteraction = false, BlocksDragArm = true,
        OnClick = c.Invoke,
        Children =
        [
            Disc(c, Rules.ColumnRing),
            R.Type.DenseMeta(c.Label) with
            {
                // The word wraps by whole words over two lines instead of shrinking or cutting ("add 2 to queue"); the column's height holds both.
                Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Command), MaxLines = 2, Wrap = TextWrap.WrapWholeWords, Trim = TextTrim.CharacterEllipsis, MinWidth = 0f,
            },
        ],
    };

    /// <summary>"select all" / "done": a word on a 40-high target (the board's 13 type).</summary>
    static Element TextCommand(string key, string label, Action invoke) => new BoxEl
    {
        Key = key, Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, MinHeight = Rules.TextTarget, MinWidth = Rules.TextTarget,
        Justify = FlexJustify.Center, Padding = new Edges4(8f, 0f, 8f, 0f),
        HoverFill = Prop.Of(static () => R.Layer.RowHover), PressedFill = Prop.Of(static () => R.Layer.RowPressed),
        Cursor = CursorId.Hand, Role = AutomationRole.Button, Focusable = true, AllowFocusOnInteraction = false, BlocksDragArm = true, OnClick = invoke,
        Children =
        [
            R.Type.DenseMeta(label) with
            {
                Color = Tok.TextPrimary, Case = R.Case.For(LabelKind.Command), MaxLines = 1, Wrap = TextWrap.NoWrap, MinWidth = 0f,
            },
        ],
    };
}
