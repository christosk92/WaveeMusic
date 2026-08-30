using System;
using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;
using SegCtl = FluentGpu.Controls.Segmented;

namespace Wavee;

/// <summary>Bespoke, dense rows for the setup wizard's decision column — deliberately NOT <see cref="SettingsCard"/>.
/// <c>SettingsCard.Build</c> stacks header/content below its own 476-DIP <c>WrapThreshold</c> and carries a 68-DIP
/// <c>MinHeight</c> (<c>SettingsCard.cs:31,129</c>); the decision column is 480 DIP wide — 4 px from tripping that
/// wrap — and the wizard's rows are 44/52 DIP, both narrower than SettingsCard's own floor. Every row here is instead a
/// flat, fixed-height <see cref="BoxEl"/> sized straight off <see cref="SetupLayout"/>'s constants, so a page's row
/// plan is a literal sum the layout tests can check without ever mounting the engine.</summary>
static class SetupCompact
{
    // ── the row chrome shared by Row/ChipRow (tokens lifted from SettingsCard.DefaultStyle) ────────────────────────
    static Edges4 RowPadding => new(SetupLayout.RowPadX, SetupLayout.RowPadY, SetupLayout.RowPadX, SetupLayout.RowPadY);

    /// <summary>One label+control row, 44 DIP tall (52 with a sub-label under the label). The label lane widens from
    /// 150 to 200 DIP when a sub-label is present (<see cref="SetupLayout.LabelLane"/>/<see cref="SetupLayout.LabelLaneSub"/>)
    /// — <see cref="SetupLayout.ControlLane"/> is the matching arithmetic for what's left for <paramref name="control"/>.
    /// <paramref name="control"/> is pinned <c>Shrink = 0</c>: a squeezed row must shrink its spacer, never quietly
    /// clip the one thing the row exists to show.</summary>
    public static Element Row(string label, Element control, string? sub = null, bool isEnabled = true)
    {
        bool hasSub = sub is { Length: > 0 };
        Element labelCol = hasSub
            ? new BoxEl
            {
                Direction = 1, Grow = 1f, Basis = 0f, MinWidth = SetupLayout.LabelLaneSub, Shrink = 1f, Justify = FlexJustify.Center,
                Children =
                [
                    new TextEl(label) { Size = 14f, LineHeight = 20f, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new TextEl(sub!) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                ],
            }
            : new BoxEl
            {
                Grow = 1f, Basis = 0f, MinWidth = SetupLayout.LabelLane, Shrink = 1f, AlignItems = FlexAlign.Center,
                Children = [new TextEl(label) { Size = 14f, LineHeight = 20f, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }],
            };

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, AlignSelf = FlexAlign.Stretch,
            Height = hasSub ? SetupLayout.RowSubHeight : SetupLayout.RowHeight,
            Gap = SetupLayout.ControlGap, Padding = RowPadding,
            Corners = Radii.ControlAll, Fill = Tok.FillCardDefault,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            IsEnabled = isEnabled,
            // `control` is typed as the abstract Element, so a plain `with { Shrink = 0f }` won't compile for every
            // leaf kind — a Shrink=0 BoxEl wrapper gets the same "never quietly shrink away" effect generically (a
            // single-child row-direction BoxEl shrink-wraps its child's own width by construction).
            Children = [labelCol, new BoxEl { Shrink = 0f, Children = [control] }],
        };
    }

    /// <summary>The one row whose label lane is content-sized rather than a fixed 150/200 — a chip row's label is
    /// short ("Extras") and its chips are the thing that needs the room, unlike every other row where the control is a
    /// single fixed-width control and the label is the long text.</summary>
    public static BoxEl ChipRow(string label, params Element[] chips) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, AlignSelf = FlexAlign.Stretch,
        Height = SetupLayout.RowHeight, Gap = SetupLayout.ControlGap, Padding = RowPadding,
        Corners = Radii.ControlAll, Fill = Tok.FillCardDefault,
        BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Children =
        [
            new TextEl(label) { Size = 12f, Weight = 600, Color = Tok.TextSecondary, Shrink = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            new BoxEl
            {
                Direction = 0, Gap = 6f, Grow = 1f, Basis = 0f, MinWidth = 0f, Wrap = true,
                Justify = FlexJustify.End, AlignItems = FlexAlign.Center,
                Children = chips,
            },
        ],
    };

    /// <summary>A whole-row click target that opens something else (a flyout, a dialog) rather than hosting an inline
    /// control — "More topics & quiet hours", "Choose a version". Plain list-row chrome
    /// (<see cref="Interaction.ListRow"/>), not the card fill/border every other row carries, so it reads as
    /// navigation rather than a value. Returns <see cref="BoxEl"/> (not <see cref="Element"/>) so a caller can hang
    /// <c>Flyout.Attach</c> off the returned node.</summary>
    public static BoxEl ClickRow(string label, string trailing, Action onClick) => new BoxEl
    {
        Direction = 0, AlignItems = FlexAlign.Center, Shrink = 0f, AlignSelf = FlexAlign.Stretch,
        Height = SetupLayout.RowHeight, Gap = SetupLayout.ControlGap, Padding = RowPadding,
        Corners = Radii.ControlAll,
        Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = onClick,
        Children =
        [
            new TextEl(label)
            {
                Size = 14f, LineHeight = 20f, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                Grow = 1f, Basis = 0f, MinWidth = SetupLayout.LabelLane, Shrink = 1f,   // the label takes what the trailing text leaves
            },
            new TextEl(trailing) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis, Shrink = 0f },
            Icon(Icons.ChevronRight, 12f, Tok.TextSecondary),
        ],
    }.Interactive(Interaction.ListRow);

    /// <summary>A toggle chip — "Artwork", "Color washes". The check glyph is ALWAYS mounted (opacity 0 when off), so
    /// toggling never reflows the chip's own width (the grammar <c>ContentFilterChips.cs</c> uses, sized down).</summary>
    public static BoxEl Chip(string label, bool on, Action toggle) => new BoxEl
    {
        Direction = 0, Height = SetupLayout.ChipHeight, Shrink = 0f, AlignItems = FlexAlign.Center,
        Padding = new Edges4(8f, 0f, 8f, 0f), Corners = Radii.FullAll,
        Fill = on ? Tok.AccentSubtle : Tok.FillControlDefault,
        HoverFill = on ? WaveeColors.SelectedHover : Tok.FillControlSecondary,
        BorderWidth = 1f, BorderColor = on ? Tok.AccentDefault : Tok.StrokeControlDefault,
        BrushTransitionMs = Motion.ControlFaster,
        Role = AutomationRole.ToggleButton, Focusable = true, Cursor = CursorId.Hand, OnClick = toggle,
        Children =
        [
            // TextEl carries no Opacity of its own (only BoxEl's channel is bindable) — a fixed-footprint BoxEl
            // wrapper fades the glyph in/out without ever changing the chip's own width.
            new BoxEl { Width = 12f, Height = 12f, Shrink = 0f, Opacity = on ? 1f : 0f, Children = [Icon(Icons.Accept, 12f, Tok.AccentTextPrimary)] },
            new BoxEl { Width = 4f },
            new TextEl(label) { Size = 12f, LineHeight = 16f, Weight = (ushort)(on ? 600 : 400), Color = Tok.TextPrimary },
        ],
    };

    /// <summary>A tight run of inline controls sharing one row slot — e.g. a mode <c>Segmented</c> beside a
    /// duration <c>ComboBox</c> that's only enabled while the mode is on.</summary>
    public static BoxEl Controls(params Element[] children) => new BoxEl
    {
        Direction = 0, Gap = SetupLayout.ControlGap, AlignItems = FlexAlign.Center, Shrink = 0f,
        Children = children,
    };

    /// <summary>A group eyebrow inside a decision column body — 20 DIP tall so it composes cleanly into a
    /// <see cref="SetupLayout.RowsHeight"/> sum when a page counts one in.</summary>
    public static BoxEl SectionLabel(string text) => new BoxEl
    {
        Height = 20f, Shrink = 0f, AlignItems = FlexAlign.Center,
        Children = [new TextEl(text) { Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis }],
    };

    /// <summary>A quiet informational card — Terms' "What Wavee needs" trio. Card chrome (not a bare paragraph) so it
    /// reads as a distinct fact rather than running prose.</summary>
    public static BoxEl InfoCard(string icon, string title, string body, int maxBodyLines = 3) => new BoxEl
    {
        Direction = 0, Gap = 10f, AlignItems = FlexAlign.Start, Shrink = 0f, AlignSelf = FlexAlign.Stretch,
        Padding = new Edges4(12f, 8f, 12f, 8f),
        Corners = Radii.ControlAll, Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Children =
        [
            Icon(icon, 16f, Tok.AccentTextPrimary) with { Margin = new Edges4(2f, 0f, 0f, 0f) },
            new BoxEl
            {
                Direction = 1, Gap = 2f, Grow = 1f, Basis = 0f, MinWidth = 0f,
                Children =
                [
                    new TextEl(title) { Size = 13f, LineHeight = 18f, Weight = 600, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new TextEl(body) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = maxBodyLines, Trim = TextTrim.WordEllipsis },
                ],
            },
        ],
    };

    /// <summary>Trademark/legal fine print — the smallest, quietest text tier in the wizard.</summary>
    public static TextEl FinePrint(string text, int maxLines = 5) => new TextEl(text)
    {
        Size = 11.5f, LineHeight = SetupLayout.FinePrintLine, Color = Tok.TextTertiary,
        Wrap = TextWrap.Wrap, MaxLines = maxLines, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
    };

    /// <summary>A vertical stack of rows with the wizard's own tight 4-DIP rhythm — deliberately not
    /// <see cref="SetupRows.Stack"/>'s Settings-tab spacing, which is looser than the 480-DIP decision column can
    /// spare.</summary>
    public static BoxEl Column(params Element[] children) => new BoxEl
    {
        Direction = 1, Gap = SetupLayout.RowGap, AlignItems = FlexAlign.Stretch, MinWidth = 0f, MinHeight = 0f,
        Children = children,
    };

    /// <summary>A flexible gap — pins whatever comes after it (fine print, a footer note) to the floor of whatever
    /// Grow-eligible container holds it.</summary>
    public static BoxEl Spacer() => new BoxEl { Grow = 1f, Shrink = 1f, MinHeight = 0f };

    /// <summary>A single-line lead paragraph inside a decision column's own body — distinct from the frame's pinned
    /// header lead (<see cref="SetupPageHost.Frame"/>'s <c>lead</c> parameter): this is for a column that wants a
    /// second, row-scoped lead sentence lower in its own flow (e.g. between a status block and its detail rows).</summary>
    public static TextEl LeadLine(string text) => new TextEl(text)
    {
        Size = 14f, LineHeight = SetupLayout.LeadLineHeight, Color = Tok.TextSecondary,
        Wrap = TextWrap.Wrap, MaxLines = 1, Trim = TextTrim.WordEllipsis, MinWidth = 0f, AlignSelf = FlexAlign.Stretch,
    };

    /// <summary><see cref="ToggleSwitch.DefaultStyle"/> is 40 DIP tall (<c>MinHeight 40</c>) and even
    /// <see cref="SettingsCard.CompactToggleStyle"/> is 36 — both taller than the 32 DIP a 44-DIP row has left after
    /// its own 6+6 vertical padding. A property (not a cached field) so it keeps re-resolving the live theme colors
    /// <see cref="ToggleSwitch.DefaultStyle"/> itself reads fresh.</summary>
    public static ToggleSwitch.Style RowToggleStyle => ToggleSwitch.DefaultStyle with { MinWidth = 0f, MinHeight = 32f };

    // ── the setup-scaled Segmented control ──────────────────────────────────────────────────────────────────────────

    /// <summary>Item root gets <c>Basis = 0</c> so <c>Grow = 1</c> on every item splits only the SURPLUS equally
    /// (true equal-width columns) — the "Theme"/"Window material" rows. The selection pill narrows from the control's
    /// default 24 DIP to 16, matching the smaller item footprint.</summary>
    static readonly TemplateParts s_equal = new()
    {
        [SegCtl.PartItem] = i => i with { Basis = 0f },
        [SegCtl.PartSelectionPill] = p => p with { Width = 16f },
    };

    /// <summary>Same pill narrowing, WITHOUT the <c>Basis = 0</c> — items keep their content-driven width instead of
    /// splitting evenly, for a row-density strip whose labels are visibly different lengths ("Compact" vs
    /// "Comfortable") and would look mis-balanced forced equal.</summary>
    static readonly TemplateParts s_content = new()
    {
        [SegCtl.PartSelectionPill] = p => p with { Width = 16f },
    };

    /// <summary>A property (not a cached field): <see cref="SegCtl.DefaultStyle"/> resolves live theme tokens fresh on
    /// every access, and caching this would freeze them at first touch.</summary>
    static SegCtl.Style SetupSegStyle => SegCtl.DefaultStyle with
    {
        Height = SetupLayout.SegmentedHeight, ItemMinWidth = 0f, FontSize = 12f, IconSize = 14f, IconGap = 6f,
        Padding = Edges4.All(2f),
        SelectedBackground = Tok.FillControlSecondary, SelectedHover = Tok.FillControlTertiary,
        SelectedFontWeight = 600,
    };

    /// <summary>The wizard's segmented control, built on <see cref="FluentGpu.Controls.Segmented"/> (NOT
    /// <c>SelectorBar</c> — its item padding + 3-px pill + bar padding run ≈48 DIP tall against a 32-DIP row, and it
    /// has no template parts to shrink that). <paramref name="selected"/> is a plain value, not a caller-owned signal:
    /// <c>SegmentedCore</c> reads its <c>SelectedIndex</c> prop live every render (re-pushed via <c>Embed.Comp</c>,
    /// never frozen at mount — verified against <c>Segmented.cs</c>), so a FRESH <c>Signal&lt;int&gt;</c> per call is
    /// re-seeded from the caller's truth every render and discarded, exactly like <see cref="WaveePicker.Strip"/>'s
    /// own throwaway signal. The real write happens in <paramref name="onChange"/> (persist, then re-render).</summary>
    public static Element Segmented(IReadOnlyList<SegmentedItem> items, int selected, Action<int> onChange,
        float width, bool equalWidth = true, bool isEnabled = true) => new BoxEl
        {
            Width = width, Direction = 1, Shrink = 0f,
            Children =
            [
                SegCtl.Create(items, new Signal<int>(selected), onChange, new SegCtl.SegmentedOptions
                {
                    IsEnabled = isEnabled,
                    Style = SetupSegStyle,
                    Parts = equalWidth ? s_equal : s_content,
                }),
            ],
        };
}
